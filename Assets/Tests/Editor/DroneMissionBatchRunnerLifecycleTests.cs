using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class DroneMissionBatchRunnerLifecycleTests
{
    private GameObject recorderObject;
    private GameObject bootstrapObject;
    private GameObject runnerObject;
    private Component recorder;
    private Component bootstrap;
    private Component runner;
    private Type recorderType;
    private Type configType;
    private string outputDirectory;

    [SetUp]
    public void SetUp()
    {
        string recoveryDirectory = Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery");
        if (Directory.Exists(recoveryDirectory)) Directory.Delete(recoveryDirectory, true);
        recorderType = RequireType("DroneMissionTelemetryRecorder, Assembly-CSharp");
        configType = RequireType("DroneMissionSessionConfig, Assembly-CSharp");
        Type bootstrapType = RequireType("DroneSwarmDemoBootstrap, Assembly-CSharp");
        Type runnerType = RequireType("DroneMissionBatchRunner, Assembly-CSharp");

        recorderObject = new GameObject("fixed-batch-recorder-test");
        recorder = recorderObject.AddComponent(recorderType);
        SetField(recorder, "outputDirectoryName", Path.Combine("DroneTelemetryTests", Guid.NewGuid().ToString("N")));
        SetField(recorder, "logOutputPathOnSessionStart", false);
        outputDirectory = GetProperty<string>(recorder, "OutputDirectory");

        bootstrapObject = new GameObject("fixed-batch-bootstrap-test");
        bootstrap = bootstrapObject.AddComponent(bootstrapType);
        SetField(bootstrap, "telemetryRecorder", recorder);

        runnerObject = new GameObject("fixed-batch-runner-test");
        runner = runnerObject.AddComponent(runnerType);
        SetField(runner, "bootstrap", bootstrap);
        SetField(runner, "telemetryRecorder", recorder);
    }

    [TearDown]
    public void TearDown()
    {
        UnityEngine.Object.DestroyImmediate(runnerObject);
        UnityEngine.Object.DestroyImmediate(bootstrapObject);
        UnityEngine.Object.DestroyImmediate(recorderObject);
        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, true);
        }
        string recoveryDirectory = Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery");
        if (Directory.Exists(recoveryDirectory)) Directory.Delete(recoveryDirectory, true);
    }

    [Test]
    public void BatchReadinessRejectsRecorderWithSummaryOutputDisabled()
    {
        SetField(recorder, "writeSummaryCsv", false);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("summary CSV output is disabled"));

        object[] arguments = { null };
        MethodInfo method = bootstrap.GetType().GetMethod("TryPrepareTelemetryForBatch", BindingFlags.Instance | BindingFlags.Public);
        Assert.That(method, Is.Not.Null);
        Assert.That((bool)method.Invoke(bootstrap, arguments), Is.False);
        Assert.That(arguments[0], Is.SameAs(recorder));
    }

    [TestCase("DroneMissionBatchRunner, Assembly-CSharp")]
    [TestCase("DroneMissionTimedBatchRunner, Assembly-CSharp")]
    public void RunnerCleanupCompletesAfterExhaustedRetryIsRecoveredByBatchReadiness(
        string runnerAssemblyName)
    {
        Type testedRunnerType = RequireType(runnerAssemblyName);
        GameObject testedObject = runnerObject;
        Component testedRunner = runner;
        if (runner.GetType() != testedRunnerType)
        {
            testedObject = new GameObject("exhausted-readiness-cleanup-timed-runner-test");
            testedRunner = testedObject.AddComponent(testedRunnerType);
            SetField(testedRunner, "bootstrap", bootstrap);
            SetField(testedRunner, "telemetryRecorder", recorder);
        }

        InvokePublic(bootstrap, "SetTelemetryBatchContext", "exhausted-readiness-batch", 1, 1, 1, true, 31);
        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", Activator.CreateInstance(configType)), Is.True);
        string summaryPath = GetProperty<string>(recorder, "SummaryFilePath");
        string originalSummary = File.ReadAllText(summaryPath);
        File.Delete(summaryPath);
        Directory.CreateDirectory(summaryPath);

        bool previousIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            InvokePublic(bootstrap, "ShutdownBatchRun", "batch_stopped", false);
            Assert.That(GetProperty<bool>(bootstrap, "IsFinalTelemetryPersistencePending"), Is.True);

            Coroutine activeRetry = (Coroutine)GetField(bootstrap, "finalTelemetryRetryCoroutine");
            Assert.That(activeRetry, Is.Not.Null);
            ((MonoBehaviour)bootstrap).StopCoroutine(activeRetry);
            SetField(bootstrap, "finalTelemetryRetryCoroutine", null);
            SetField(bootstrap, "finalTelemetryRetryAttemptsCompleted", 3);
            IEnumerator exhaustedRetry = (IEnumerator)InvokePrivate(bootstrap, "RetryFinalTelemetryPersistence");
            Assert.That(exhaustedRetry.MoveNext(), Is.False);
            Assert.That(GetProperty<bool>(bootstrap, "HasTerminalTelemetryPersistenceFailure"), Is.True);

            Directory.Delete(summaryPath, true);
            File.WriteAllText(summaryPath, originalSummary);
            object[] readinessArguments = { null };
            MethodInfo readiness = bootstrap.GetType().GetMethod(
                "TryPrepareTelemetryForBatch",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That((bool)readiness.Invoke(bootstrap, readinessArguments), Is.True);
            Assert.That(readinessArguments[0], Is.SameAs(recorder));
            Assert.That(GetProperty<bool>(recorder, "HasActiveSession"), Is.False,
                "readiness recovery must not require a replacement session");
            Assert.That(GetProperty<bool>(bootstrap, "IsFinalTelemetryPersistencePending"), Is.False);
            Assert.That(GetProperty<bool>(bootstrap, "HasTerminalTelemetryPersistenceFailure"), Is.False);
            Assert.That((bool)GetField(bootstrap, "finalTelemetryRetryExhausted"), Is.False);

            InvokePrivate(testedRunner, "CleanupBatch", "batch_stopped");

            Assert.That((bool)GetField(testedRunner, "batchCleanupCompleted"), Is.True);
            Assert.That((bool)GetField(testedRunner, "telemetryCleanupPending"), Is.False);
            Assert.That(GetProperty<bool>(testedRunner, "IsRunning"), Is.False);
            Assert.That((string)GetField(bootstrap, "telemetryBatchId"), Is.Empty);
        }
        finally
        {
            LogAssert.ignoreFailingMessages = previousIgnore;
            if (testedObject != runnerObject)
            {
                UnityEngine.Object.DestroyImmediate(testedObject);
            }
        }
    }

    [Test]
    public void ExpiredTimedBatchDeadlineEndsActiveMissionWithWallClockReason()
    {
        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", Activator.CreateInstance(configType)), Is.True);
        var timedRunnerObject = new GameObject("timed-batch-deadline-test");
        var activeDroneObject = new GameObject("timed-batch-active-drone-test");
        Component activeDrone = activeDroneObject.AddComponent(RequireType("DroneFrontierExplorer, Assembly-CSharp"));
        Component timedRunner = timedRunnerObject.AddComponent(RequireType("DroneMissionTimedBatchRunner, Assembly-CSharp"));
        ((IList)GetField(bootstrap, "explorers")).Add(activeDrone);
        SetField(timedRunner, "bootstrap", bootstrap);
        SetField(timedRunner, "telemetryRecorder", recorder);
        SetField(timedRunner, "allowActiveMissionToFinishAfterSegment", true);

        try
        {
            IEnumerator wait = (IEnumerator)InvokePrivate(
                timedRunner,
                "WaitForActiveSessionToEnd",
                "timed-deadline-test",
                1,
                Time.realtimeSinceStartup + 3600f,
                Time.realtimeSinceStartup - 1f,
                float.PositiveInfinity);
            Assert.That(wait.MoveNext(), Is.False);

            Assert.That(GetProperty<bool>(recorder, "HasActiveSession"), Is.False);
            Assert.That((bool)GetField(timedRunner, "wallClockTimeoutReached"), Is.True);
            Assert.That(((Behaviour)activeDrone).enabled, Is.False);
            Assert.That(((Behaviour)activeDroneObject.GetComponent(RequireType("DroneGridSensor, Assembly-CSharp"))).enabled, Is.False);
            Assert.That(((Behaviour)activeDroneObject.GetComponent(RequireType("DronePathFollower, Assembly-CSharp"))).enabled, Is.False);
            string[] rows = File.ReadAllLines(GetProperty<string>(recorder, "SummaryFilePath"));
            Assert.That(rows.Count(line => line.Contains(",wall_clock_timeout,")), Is.EqualTo(1));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(activeDroneObject);
            UnityEngine.Object.DestroyImmediate(timedRunnerObject);
        }
    }

    [Test]
    public void BatchSoftTelemetryTimeoutFreezesRuntimeBeforeHardDeadlineAndAllowsCleanReset()
    {
        var activeDroneObject = new GameObject("soft-timeout-drone-test");
        Component activeDrone = activeDroneObject.AddComponent(RequireType("DroneFrontierExplorer, Assembly-CSharp"));
        Component commandPlanner = activeDroneObject.AddComponent(RequireType("DroneCommandRoutePlanner, Assembly-CSharp"));
        var activeHumanObject = new GameObject("soft-timeout-human-test");
        activeHumanObject.AddComponent<CharacterController>();
        Component activeHuman = activeHumanObject.AddComponent(RequireType("Explorer, Assembly-CSharp"));
        SetField(bootstrap, "humanExplorer", activeHuman);
        var timedRunnerObject = new GameObject("soft-timeout-timed-runner-test");
        Component timedRunner = timedRunnerObject.AddComponent(RequireType("DroneMissionTimedBatchRunner, Assembly-CSharp"));

        ((IList)GetField(bootstrap, "explorers")).Add(activeDrone);
        SetField(bootstrap, "commandRoutePlanner", commandPlanner);
        SetField(timedRunner, "bootstrap", bootstrap);
        SetField(timedRunner, "telemetryRecorder", recorder);
        InvokePublic(bootstrap, "SetTelemetryBatchContext", "soft-timeout-batch", 1, 1, 1, true, 17);
        InvokePublic(bootstrap, "ConfigureExperiment", 1, 1, 1f, 1f,
            Enum.Parse(RequireType("DroneNative+PlannerType, Assembly-CSharp"), "AStar"), 1f, true);
        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", Activator.CreateInstance(configType)), Is.True);
        SetField(recorder, "sessionStartTime", Time.time - 2f);

        try
        {
            InvokePrivate(bootstrap, "UpdateTelemetryTimeout");

            Assert.That(GetProperty<bool>(recorder, "HasActiveSession"), Is.False);
            Assert.That(((Behaviour)activeDrone).enabled, Is.False);
            Assert.That(((Behaviour)activeDroneObject.GetComponent(RequireType("DroneGridSensor, Assembly-CSharp"))).enabled, Is.False);
            Assert.That(((Behaviour)activeDroneObject.GetComponent(RequireType("DronePathFollower, Assembly-CSharp"))).enabled, Is.False);
            Assert.That(((Behaviour)commandPlanner).enabled, Is.False);
            Assert.That(((Behaviour)activeHuman).enabled, Is.False);
            Assert.That(File.ReadAllLines(GetProperty<string>(recorder, "SummaryFilePath"))
                .Count(line => line.Contains(",timeout,")), Is.EqualTo(1));

            float futureHardDeadline = Time.realtimeSinceStartup + 60f;
            IEnumerator fixedWait = (IEnumerator)InvokePrivate(
                runner, "WaitForActiveSessionToEnd", "soft-timeout-batch", 1, futureHardDeadline);
            Assert.That(fixedWait.MoveNext(), Is.False);
            Assert.That((bool)GetField(runner, "runHardTimeoutReached"), Is.False);

            IEnumerator timedWait = (IEnumerator)InvokePrivate(
                timedRunner,
                "WaitForActiveSessionToEnd",
                "soft-timeout-batch",
                1,
                Time.realtimeSinceStartup + 60f,
                float.PositiveInfinity,
                futureHardDeadline);
            Assert.That(timedWait.MoveNext(), Is.False);
            Assert.That((bool)GetField(timedRunner, "wallClockTimeoutReached"), Is.False);

            Assert.That((bool)InvokePublic(bootstrap, "TryResetDemo"), Is.True,
                "the batch-only freeze must not latch interactive gameplay stopped");
            Assert.That((bool)GetField(bootstrap, "batchRunShutdownApplied"), Is.False);
            Assert.That(((Behaviour)activeDrone).enabled, Is.False,
                "the old world must remain frozen until replacement");
            InvokePublic(bootstrap, "CancelQueuedReset", "test cleanup");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(activeHumanObject);
            UnityEngine.Object.DestroyImmediate(activeDroneObject);
            UnityEngine.Object.DestroyImmediate(timedRunnerObject);
        }
    }

    [Test]
    public void TimedSegmentCutoffFreezesComponentsAndPersistsSegmentTimeout()
    {
        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", Activator.CreateInstance(configType)), Is.True);
        var timedRunnerObject = new GameObject("timed-segment-cutoff-test");
        var activeDroneObject = new GameObject("timed-segment-cutoff-drone-test");
        Component activeDrone = activeDroneObject.AddComponent(RequireType("DroneFrontierExplorer, Assembly-CSharp"));
        Component commandPlanner = activeDroneObject.AddComponent(RequireType("DroneCommandRoutePlanner, Assembly-CSharp"));
        var activeHumanObject = new GameObject("timed-segment-cutoff-human-test");
        activeHumanObject.AddComponent<CharacterController>();
        Component activeHuman = activeHumanObject.AddComponent(RequireType("Explorer, Assembly-CSharp"));
        SetField(bootstrap, "humanExplorer", activeHuman);
        Component timedRunner = timedRunnerObject.AddComponent(RequireType("DroneMissionTimedBatchRunner, Assembly-CSharp"));
        ((IList)GetField(bootstrap, "explorers")).Add(activeDrone);
        SetField(bootstrap, "commandRoutePlanner", commandPlanner);
        SetField(timedRunner, "bootstrap", bootstrap);
        SetField(timedRunner, "telemetryRecorder", recorder);
        SetField(timedRunner, "allowActiveMissionToFinishAfterSegment", false);

        try
        {
            IEnumerator wait = (IEnumerator)InvokePrivate(
                timedRunner,
                "WaitForActiveSessionToEnd",
                "timed-segment-cutoff",
                1,
                Time.realtimeSinceStartup - 1f,
                float.PositiveInfinity,
                float.PositiveInfinity);
            Assert.That(wait.MoveNext(), Is.True);
            Assert.That(wait.MoveNext(), Is.False);

            Assert.That(GetProperty<bool>(recorder, "HasActiveSession"), Is.False);
            Assert.That(((Behaviour)activeDrone).enabled, Is.False);
            Assert.That(((Behaviour)activeDroneObject.GetComponent(RequireType("DroneGridSensor, Assembly-CSharp"))).enabled, Is.False);
            Assert.That(((Behaviour)activeDroneObject.GetComponent(RequireType("DronePathFollower, Assembly-CSharp"))).enabled, Is.False);
            Assert.That(((Behaviour)commandPlanner).enabled, Is.False);
            Assert.That(((Behaviour)activeHuman).enabled, Is.False);
            Assert.That(File.ReadAllLines(GetProperty<string>(recorder, "SummaryFilePath"))
                .Count(line => line.Contains(",segment_timeout,")), Is.EqualTo(1));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(activeHumanObject);
            UnityEngine.Object.DestroyImmediate(activeDroneObject);
            UnityEngine.Object.DestroyImmediate(timedRunnerObject);
        }
    }

    [Test]
    public void TimedSegmentDeadlineCrossedDuringPreparationBlocksMissionLaunch()
    {
        var timedRunnerObject = new GameObject("timed-preparation-deadline-test");
        Component timedRunner = timedRunnerObject.AddComponent(RequireType("DroneMissionTimedBatchRunner, Assembly-CSharp"));
        SetField(timedRunner, "bootstrap", bootstrap);
        SetField(timedRunner, "telemetryRecorder", recorder);

        try
        {
            Assert.That((bool)InvokePrivate(
                timedRunner,
                "TryReachSegmentDeadline",
                Time.realtimeSinceStartup - 1f,
                false), Is.True);
            Assert.That(GetProperty<bool>(bootstrap, "IsTelemetrySessionActive"), Is.False);
            Assert.That(GetProperty<bool>(bootstrap, "IsResetQueued"), Is.False);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(timedRunnerObject);
        }
    }

    [Test]
    public void TimedSegmentDeadlineCrossedDuringQueuedResetCancelsPendingLaunch()
    {
        var timedRunnerObject = new GameObject("timed-reset-deadline-test");
        Component timedRunner = timedRunnerObject.AddComponent(RequireType("DroneMissionTimedBatchRunner, Assembly-CSharp"));
        SetField(timedRunner, "bootstrap", bootstrap);
        SetField(timedRunner, "telemetryRecorder", recorder);

        try
        {
            Assert.That((bool)InvokePublic(bootstrap, "TryResetDemo"), Is.True);
            Assert.That(GetProperty<bool>(bootstrap, "IsResetQueued"), Is.True);

            Assert.That((bool)InvokePrivate(
                timedRunner,
                "TryReachSegmentDeadline",
                Time.realtimeSinceStartup - 1f,
                false), Is.True);

            Assert.That(GetProperty<bool>(bootstrap, "IsResetQueued"), Is.False);
            Assert.That(GetProperty<bool>(bootstrap, "IsTelemetrySessionActive"), Is.False);
            Assert.That(GetProperty<bool>(bootstrap, "LastResetSucceeded"), Is.False);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(timedRunnerObject);
        }
    }

    [Test]
    public void TimedSegmentDeadlineCrossedOnStartupFinalYieldRejectsLateSession()
    {
        var timedRunnerObject = new GameObject("timed-startup-final-yield-deadline-test");
        Component timedRunner = timedRunnerObject.AddComponent(RequireType("DroneMissionTimedBatchRunner, Assembly-CSharp"));
        SetField(timedRunner, "bootstrap", bootstrap);
        SetField(timedRunner, "telemetryRecorder", recorder);
        SetField(timedRunner, "allowActiveMissionToFinishAfterSegment", true);
        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", Activator.CreateInstance(configType)), Is.True);

        try
        {
            float expiredDeadline = Time.realtimeSinceStartup - 1f;
            bool acceptedBeforeDeadline = (bool)InvokePrivate(
                timedRunner,
                "HasActiveMissionStartedBeforeDeadline",
                expiredDeadline);
            Assert.That(acceptedBeforeDeadline, Is.False);
            Assert.That((bool)InvokePrivate(
                timedRunner,
                "TryReachSegmentDeadline",
                expiredDeadline,
                acceptedBeforeDeadline), Is.True);

            Assert.That(GetProperty<bool>(bootstrap, "IsTelemetrySessionActive"), Is.False,
                "a session first observed after expiry is not an active pre-deadline mission");
            Assert.That(File.ReadAllLines(GetProperty<string>(recorder, "SummaryFilePath"))
                .Count(line => line.Contains(",segment_timeout,")), Is.EqualTo(1));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(timedRunnerObject);
        }
    }

    [Test]
    public void TimedSegmentDeadlineBoundsInterMissionDelayButPreservesAcceptedMissionPolicy()
    {
        var timedRunnerObject = new GameObject("timed-inter-mission-deadline-test");
        Component timedRunner = timedRunnerObject.AddComponent(RequireType("DroneMissionTimedBatchRunner, Assembly-CSharp"));
        SetField(timedRunner, "bootstrap", bootstrap);
        SetField(timedRunner, "telemetryRecorder", recorder);
        SetField(timedRunner, "allowActiveMissionToFinishAfterSegment", true);

        try
        {
            Assert.That((bool)InvokePrivate(
                timedRunner,
                "TryReachSegmentDeadline",
                Time.realtimeSinceStartup - 1f,
                false), Is.True,
                "an inter-mission wait must stop at the segment deadline");

            Assert.That((bool)InvokePublic(recorder, "TryBeginSession", Activator.CreateInstance(configType)), Is.True);
            float activeMissionDeadline = Time.realtimeSinceStartup + 0.01f;
            bool acceptedBeforeDeadline = (bool)InvokePrivate(
                timedRunner,
                "HasActiveMissionStartedBeforeDeadline",
                activeMissionDeadline);
            Assert.That(acceptedBeforeDeadline, Is.True);
            System.Threading.Thread.Sleep(20);
            Assert.That(Time.realtimeSinceStartup, Is.GreaterThanOrEqualTo(activeMissionDeadline));
            Assert.That((bool)InvokePrivate(
                timedRunner,
                "TryReachSegmentDeadline",
                activeMissionDeadline,
                acceptedBeforeDeadline), Is.True);
            Assert.That(GetProperty<bool>(bootstrap, "IsTelemetrySessionActive"), Is.True,
                "the overrun policy applies only when the mission was accepted before expiry");
            InvokePublic(bootstrap, "ShutdownBatchRun", "test_cleanup", false);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(timedRunnerObject);
        }
    }

    [Test]
    public void TimedRunnerWaitsForTransientFinalPersistenceBeforeDurableRecount()
    {
        var timedRunnerObject = new GameObject("timed-transient-final-persistence-test");
        Component timedRunner = timedRunnerObject.AddComponent(RequireType("DroneMissionTimedBatchRunner, Assembly-CSharp"));
        SetField(timedRunner, "bootstrap", bootstrap);
        SetField(timedRunner, "telemetryRecorder", recorder);
        InvokePublic(bootstrap, "SetTelemetryBatchContext", "timed-transient-batch", 1, 1, 1, true, 5);

        object config = Activator.CreateInstance(configType);
        configType.GetField("BatchId").SetValue(config, "timed-transient-batch");
        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", config), Is.True);
        string summaryPath = GetProperty<string>(recorder, "SummaryFilePath");
        string originalSummary = File.ReadAllText(summaryPath);
        File.Delete(summaryPath);
        Directory.CreateDirectory(summaryPath);

        bool previousIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            InvokePublic(bootstrap, "ShutdownBatchRun", "mission_complete", true);
            Assert.That(GetProperty<bool>(bootstrap, "IsFinalTelemetryPersistencePending"), Is.True);

            Assert.That((int)InvokePrivate(timedRunner, "CountValidMissions", "timed-transient-batch"), Is.Zero);
            Assert.That((bool)GetField(timedRunner, "telemetryPersistenceFailed"), Is.False,
                "sticky write failure is recoverable while final persistence is pending");

            float persistenceDeadline = Time.realtimeSinceStartup + 60f;
            IEnumerator fixedWait = (IEnumerator)InvokePrivate(
                runner,
                "WaitForActiveSessionToEnd",
                "timed-transient-batch",
                1,
                persistenceDeadline);
            IEnumerator wait = (IEnumerator)InvokePrivate(
                timedRunner,
                "WaitForActiveSessionToEnd",
                "timed-transient-batch",
                1,
                Time.realtimeSinceStartup + 60f,
                persistenceDeadline,
                persistenceDeadline);
            Assert.That(fixedWait.MoveNext(), Is.True,
                "fixed runner must not prepare the next world while final persistence is pending");
            Assert.That(wait.MoveNext(), Is.True,
                "timed runner must not prepare the next world while final persistence is pending");

            Directory.Delete(summaryPath, true);
            File.WriteAllText(summaryPath, originalSummary);
            Assert.That((bool)InvokePublic(bootstrap, "EndActiveTelemetrySession", "ignored_retry_reason", false), Is.True);
            Assert.That(fixedWait.MoveNext(), Is.False);
            Assert.That(wait.MoveNext(), Is.False);

            Assert.That((int)InvokePrivate(timedRunner, "CountValidMissions", "timed-transient-batch"), Is.EqualTo(1));
            Assert.That((bool)GetField(timedRunner, "telemetryPersistenceFailed"), Is.False);
            Assert.That(File.ReadAllLines(summaryPath)
                .Count(line => line.Contains(",mission_complete,")), Is.EqualTo(1));
        }
        finally
        {
            LogAssert.ignoreFailingMessages = previousIgnore;
            UnityEngine.Object.DestroyImmediate(timedRunnerObject);
        }
    }

    [Test]
    public void TimedRunHardDeadlineKeepsWorldFrozenUntilTransientFinalWriteRecovers()
    {
        var timedRunnerObject = new GameObject("timed-hard-deadline-persistence-gate-test");
        var activeDroneObject = new GameObject("timed-hard-deadline-persistence-drone-test");
        var oldWorldObject = new GameObject("timed-hard-deadline-old-world-test");
        Component activeDrone = activeDroneObject.AddComponent(RequireType("DroneFrontierExplorer, Assembly-CSharp"));
        Component timedRunner = timedRunnerObject.AddComponent(RequireType("DroneMissionTimedBatchRunner, Assembly-CSharp"));
        ((IList)GetField(bootstrap, "explorers")).Add(activeDrone);
        InvokePrivate(bootstrap, "RegisterSpawned", oldWorldObject);
        SetField(timedRunner, "bootstrap", bootstrap);
        SetField(timedRunner, "telemetryRecorder", recorder);
        InvokePublic(bootstrap, "SetTelemetryBatchContext", "timed-hard-retry-batch", 1, 1, 1, true, 23);

        object config = Activator.CreateInstance(configType);
        configType.GetField("BatchId").SetValue(config, "timed-hard-retry-batch");
        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", config), Is.True);
        string summaryPath = GetProperty<string>(recorder, "SummaryFilePath");
        string originalSummary = File.ReadAllText(summaryPath);
        File.Delete(summaryPath);
        Directory.CreateDirectory(summaryPath);

        int resetWorldMutationCount = 0;
        SetField(bootstrap, "resetRuntimeSetupExceptionInjection", (Action<string>)(stage =>
        {
            if (stage == "world") resetWorldMutationCount++;
        }));
        bool previousIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            IEnumerator wait = (IEnumerator)InvokePrivate(
                timedRunner,
                "WaitForActiveSessionToEnd",
                "timed-hard-retry-batch",
                1,
                Time.realtimeSinceStartup + 3600f,
                float.PositiveInfinity,
                Time.realtimeSinceStartup - 1f);

            Assert.That(wait.MoveNext(), Is.True,
                "run-hard shutdown must hand control to the final-persistence resolution gate");
            IEnumerator persistenceGate = wait.Current as IEnumerator;
            Assert.That(persistenceGate, Is.Not.Null);
            Assert.That(GetProperty<bool>(bootstrap, "IsFinalTelemetryPersistencePending"), Is.True);
            Assert.That(GetProperty<bool>(bootstrap, "IsResetQueued"), Is.False);
            Assert.That(oldWorldObject.activeSelf, Is.True,
                "the frozen old world must not be retired before final rows are durable");
            Assert.That(((Behaviour)activeDrone).enabled, Is.False);

            Assert.That(persistenceGate.MoveNext(), Is.True);
            Assert.That(resetWorldMutationCount, Is.Zero);
            Assert.That(GetProperty<bool>(bootstrap, "IsResetQueued"), Is.False,
                "the next reset must not be queued while the bounded retry is pending");
            Assert.That(oldWorldObject.activeSelf, Is.True);

            Directory.Delete(summaryPath, true);
            File.WriteAllText(summaryPath, originalSummary);
            Assert.That((bool)InvokePublic(
                bootstrap, "EndActiveTelemetrySession", "ignored_retry_reason", false), Is.True);
            Assert.That(persistenceGate.MoveNext(), Is.False);
            Assert.That(wait.MoveNext(), Is.False);

            Assert.That(GetProperty<bool>(bootstrap, "IsFinalTelemetryPersistencePending"), Is.False);
            Assert.That(resetWorldMutationCount, Is.Zero);
            Assert.That(GetProperty<bool>(bootstrap, "IsResetQueued"), Is.False);
            Assert.That(oldWorldObject.activeSelf, Is.True);
            Assert.That(File.ReadAllLines(summaryPath)
                .Count(line => line.Contains(",run_hard_timeout,")), Is.EqualTo(1));
        }
        finally
        {
            LogAssert.ignoreFailingMessages = previousIgnore;
            SetField(bootstrap, "resetRuntimeSetupExceptionInjection", null);
            UnityEngine.Object.DestroyImmediate(activeDroneObject);
            UnityEngine.Object.DestroyImmediate(oldWorldObject);
            UnityEngine.Object.DestroyImmediate(timedRunnerObject);
        }
    }

    [Test]
    public void TimedBatchRunHardDeadlineCannotBeDisabledWithUnlimitedGlobalAndZeroSoftTimeout()
    {
        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", Activator.CreateInstance(configType)), Is.True);
        var timedRunnerObject = new GameObject("timed-batch-hard-deadline-test");
        var activeDroneObject = new GameObject("timed-batch-hard-deadline-drone-test");
        Component activeDrone = activeDroneObject.AddComponent(RequireType("DroneFrontierExplorer, Assembly-CSharp"));
        Component timedRunner = timedRunnerObject.AddComponent(RequireType("DroneMissionTimedBatchRunner, Assembly-CSharp"));
        ((IList)GetField(bootstrap, "explorers")).Add(activeDrone);
        SetField(timedRunner, "bootstrap", bootstrap);
        SetField(timedRunner, "telemetryRecorder", recorder);
        SetField(timedRunner, "allowActiveMissionToFinishAfterSegment", true);
        SetField(timedRunner, "maxWallClockHours", 0f);
        SetField(timedRunner, "perSessionTimeoutSeconds", 0f);
        SetField(timedRunner, "runHardTimeoutSeconds", 0f);

        try
        {
            Assert.That((float)InvokePrivate(timedRunner, "GetValidRunHardTimeoutSeconds"), Is.EqualTo(1f));
            InvokePrivate(timedRunner, "OnValidate");
            Assert.That((float)GetField(timedRunner, "runHardTimeoutSeconds"), Is.EqualTo(1f));

            IEnumerator wait = (IEnumerator)InvokePrivate(
                timedRunner,
                "WaitForActiveSessionToEnd",
                "timed-hard-deadline-test",
                1,
                Time.realtimeSinceStartup - 1f,
                float.PositiveInfinity,
                Time.realtimeSinceStartup - 1f);
            Assert.That(wait.MoveNext(), Is.False);

            Assert.That(GetProperty<bool>(recorder, "HasActiveSession"), Is.False);
            Assert.That((bool)GetField(timedRunner, "wallClockTimeoutReached"), Is.False);
            Assert.That(((Behaviour)activeDrone).enabled, Is.False);
            Assert.That(((Behaviour)activeDroneObject.GetComponent(RequireType("DroneGridSensor, Assembly-CSharp"))).enabled, Is.False);
            Assert.That(((Behaviour)activeDroneObject.GetComponent(RequireType("DronePathFollower, Assembly-CSharp"))).enabled, Is.False);
            string[] rows = File.ReadAllLines(GetProperty<string>(recorder, "SummaryFilePath"));
            Assert.That(rows.Count(line => line.Contains(",run_hard_timeout,")), Is.EqualTo(1));
            Assert.That(rows.Any(line => line.Contains(",segment_timeout,")), Is.False);
            Assert.That(rows.Any(line => line.Contains(",wall_clock_timeout,")), Is.False);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(activeDroneObject);
            UnityEngine.Object.DestroyImmediate(timedRunnerObject);
        }
    }

    [Test]
    public void ExplicitNewGameBoundaryPreflightsBeforeRetiringResultWorld()
    {
        SetField(bootstrap, "telemetryEnabled", false);
        SetField(bootstrap, "gameplayStopped", true);
        var resultWorldObject = new GameObject("retained-result-world-test");
        InvokePrivate(bootstrap, "RegisterSpawned", resultWorldObject);

        Assert.That((bool)InvokePublic(bootstrap, "TryPrepareForNewGame"), Is.True);
        Assert.That(GetProperty<bool>(bootstrap, "IsGameplayStopped"), Is.True);
        Assert.That(resultWorldObject.activeSelf, Is.True);

        Assert.That((bool)InvokePublic(bootstrap, "TryActivatePreparedNewGame"), Is.True);
        Assert.That(GetProperty<bool>(bootstrap, "IsGameplayStopped"), Is.False);
        Assert.That(resultWorldObject.activeSelf, Is.True,
            "activation must retain result objects until the replacement reset commits");

        InvokePublic(bootstrap, "CommitPreparedNewGame");
        Assert.That(resultWorldObject == null || !resultWorldObject.activeSelf, Is.True);
        UnityEngine.Object.DestroyImmediate(resultWorldObject);
    }

    [Test]
    public void FailedReplayPreflightPreservesRetainedInteractiveWorld()
    {
        TerrainData terrainData = null;
        GameObject terrainObject = null;
        GameObject terrainGeneratorObject = null;
        GameObject forestObject = null;
        GameObject explorerObject = null;
        GameObject scriptsObject = null;
        GameObject uiObject = null;
        GameObject resultDrone = null;
        string blockedOutputPath = null;

        try
        {
            terrainData = new TerrainData
            {
                heightmapResolution = 33,
                size = new Vector3(64f, 12f, 64f)
            };
            var retainedHeights = new float[33, 33];
            retainedHeights[16, 16] = 0.75f;
            terrainData.SetHeights(0, 0, retainedHeights);
            terrainObject = Terrain.CreateTerrainGameObject(terrainData);
            Terrain terrain = terrainObject.GetComponent<Terrain>();

            terrainGeneratorObject = new GameObject("replay-preflight-terrain-generator-test");
            Component terrainGenerator = terrainGeneratorObject.AddComponent(RequireType("TerrainGenerator, Assembly-CSharp"));
            SetField(terrainGenerator, "terrain", terrain);
            SetField(terrainGenerator, "widthx", 64);
            SetField(terrainGenerator, "widthz", 64);

            forestObject = new GameObject("replay-preflight-forest-test");
            Component forest = forestObject.AddComponent(RequireType("ForestSpawner, Assembly-CSharp"));
            SetField(forest, "terrain", terrain);
            SetField(forest, "objectsPer100SquareMeters", 0f);
            var retainedTree = new GameObject("retained-tree-test");
            retainedTree.transform.SetParent(forestObject.transform, false);
            retainedTree.transform.position = new Vector3(7f, 2f, 9f);

            explorerObject = new GameObject("retained-explorer-test");
            explorerObject.AddComponent<CharacterController>();
            Component explorer = explorerObject.AddComponent(RequireType("Explorer, Assembly-CSharp"));
            SetField(explorer, "terrain", terrain);
            SetField(explorer, "forestSpawner", forest);
            explorerObject.transform.position = new Vector3(13f, 3f, 17f);
            ((Behaviour)explorer).enabled = false;

            resultDrone = new GameObject("retained-drone-test");
            resultDrone.transform.position = new Vector3(23f, 5f, 29f);
            InvokePrivate(bootstrap, "RegisterSpawned", resultDrone);
            SetField(bootstrap, "gameplayStopped", true);

            // A file where the recorder needs a directory forces storage preflight
            // failure without creating or replacing any prior session data.
            string relativeOutput = Path.Combine("DroneTelemetryTests", Guid.NewGuid().ToString("N"), "blocked");
            SetField(recorder, "outputDirectoryName", relativeOutput);
            SetField(recorder, "writeEventCsv", false);
            blockedOutputPath = GetProperty<string>(recorder, "OutputDirectory");
            Directory.CreateDirectory(Path.GetDirectoryName(blockedOutputPath));
            File.WriteAllText(blockedOutputPath, "do-not-replace");

            uiObject = new GameObject("replay-preflight-ui-test");
            Component ui = uiObject.AddComponent(RequireType("UiScriptsControl, Assembly-CSharp"));
            scriptsObject = new GameObject("replay-preflight-scripts-test");
            Component scripts = scriptsObject.AddComponent(RequireType("ScriptsControl, Assembly-CSharp"));
            SetField(scripts, "terrainGenerator", terrainGenerator);
            SetField(scripts, "forestSpawner", forest);
            SetField(scripts, "droneSwarmDemoBootstrap", bootstrap);
            SetField(scripts, "explorer", explorer);
            SetField(scripts, "uiScriptsControl", ui);

            Vector3 explorerPosition = explorerObject.transform.position;
            Vector3 dronePosition = resultDrone.transform.position;
            Vector3 treePosition = retainedTree.transform.position;
            float retainedCenterHeight = terrainData.GetHeight(16, 16);

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to validate/create schema"));
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Replay storage validation failed"));
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Replay preflight failed"));
            Assert.That((bool)InvokePublic(scripts, "TryStartNewSimulation"), Is.False);

            Assert.That(terrainData.GetHeight(16, 16), Is.EqualTo(retainedCenterHeight));
            Assert.That(retainedTree.activeSelf, Is.True);
            Assert.That(retainedTree.transform.position, Is.EqualTo(treePosition));
            Assert.That(explorerObject.transform.position, Is.EqualTo(explorerPosition));
            Assert.That(((Behaviour)explorer).enabled, Is.False);
            Assert.That(resultDrone.activeSelf, Is.True);
            Assert.That(resultDrone.transform.position, Is.EqualTo(dronePosition));
            Assert.That(GetProperty<bool>(bootstrap, "IsGameplayStopped"), Is.True);
            Assert.That(GetProperty<bool>(bootstrap, "IsTelemetrySessionActive"), Is.False);
            Assert.That(File.ReadAllText(blockedOutputPath), Is.EqualTo("do-not-replace"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(scriptsObject);
            UnityEngine.Object.DestroyImmediate(uiObject);
            UnityEngine.Object.DestroyImmediate(explorerObject);
            UnityEngine.Object.DestroyImmediate(forestObject);
            UnityEngine.Object.DestroyImmediate(terrainGeneratorObject);
            UnityEngine.Object.DestroyImmediate(terrainObject);
            UnityEngine.Object.DestroyImmediate(terrainData);
            if (!string.IsNullOrEmpty(blockedOutputPath) && File.Exists(blockedOutputPath))
            {
                File.Delete(blockedOutputPath);
            }
            if (resultDrone != null)
            {
                UnityEngine.Object.DestroyImmediate(resultDrone);
            }
        }
    }

    [Test]
    public void StagedExplorerSpawnIgnoresOldForestColliderAndRestoresColliderStates()
    {
        TerrainData stagedData = null;
        GameObject stagedTerrainObject = null;
        GameObject retainedForestObject = null;
        GameObject stagedForestObject = null;
        GameObject explorerObject = null;
        GameObject scriptsObject = null;

        try
        {
            stagedData = new TerrainData
            {
                heightmapResolution = 33,
                size = new Vector3(20f, 5f, 20f)
            };
            stagedTerrainObject = Terrain.CreateTerrainGameObject(stagedData);
            stagedTerrainObject.transform.position = new Vector3(200f, 0f, 200f);
            Terrain stagedTerrain = stagedTerrainObject.GetComponent<Terrain>();

            retainedForestObject = new GameObject("retained-forest-collider-isolation-test");
            Component retainedForest = retainedForestObject.AddComponent(RequireType("ForestSpawner, Assembly-CSharp"));
            var oldObstacle = new GameObject("old-only-spawn-obstacle-test") { layer = 7 };
            oldObstacle.transform.SetParent(retainedForestObject.transform, false);
            oldObstacle.transform.position = new Vector3(210f, 1f, 210f);
            BoxCollider oldEnabledCollider = oldObstacle.AddComponent<BoxCollider>();
            oldEnabledCollider.size = new Vector3(6f, 6f, 6f);
            BoxCollider oldDisabledCollider = oldObstacle.AddComponent<BoxCollider>();
            oldDisabledCollider.enabled = false;

            stagedForestObject = new GameObject("staged-forest-collider-isolation-test");
            Component stagedForest = stagedForestObject.AddComponent(RequireType("ForestSpawner, Assembly-CSharp"));
            SetField(stagedForest, "terrain", stagedTerrain);

            explorerObject = new GameObject("staged-spawn-explorer-collider-isolation-test");
            explorerObject.AddComponent<CharacterController>();
            Component explorer = explorerObject.AddComponent(RequireType("Explorer, Assembly-CSharp"));
            SetField(explorer, "terrainMargin", 9f);
            SetField(explorer, "initialSpawnCheckRadius", 0.25f);
            SetField(explorer, "initialSpawnMaxAttempts", 4);
            SetField(explorer, "initialTreeDistance", 0f);
            SetField(explorer, "obstacleMask", (LayerMask)(1 << 7));

            scriptsObject = new GameObject("staged-spawn-scripts-collider-isolation-test");
            Component scripts = scriptsObject.AddComponent(RequireType("ScriptsControl, Assembly-CSharp"));
            SetField(scripts, "forestSpawner", retainedForest);
            SetField(scripts, "explorer", explorer);

            Physics.SyncTransforms();
            Assert.That(
                Physics.CheckSphere(new Vector3(210f, 1.1f, 210f), 1.5f, 1 << 7, QueryTriggerInteraction.Collide),
                Is.True,
                "the retained-only obstacle must overlap the staged spawn area before preparation");

            object[] arguments = { stagedTerrain, stagedForest, null };
            Assert.That(
                (bool)InvokePrivate(scripts, "TryPrepareExplorerSpawnAgainstStagedWorld", arguments),
                Is.True,
                "an old-only forest obstacle must not reject a valid staged-world spawn");
            Vector3 spawnPosition = (Vector3)arguments[2];
            Assert.That(spawnPosition.x, Is.InRange(209f, 211f));
            Assert.That(spawnPosition.z, Is.InRange(209f, 211f));
            Assert.That(oldEnabledCollider.enabled, Is.True);
            Assert.That(oldDisabledCollider.enabled, Is.False);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(scriptsObject);
            UnityEngine.Object.DestroyImmediate(explorerObject);
            UnityEngine.Object.DestroyImmediate(stagedForestObject);
            UnityEngine.Object.DestroyImmediate(retainedForestObject);
            UnityEngine.Object.DestroyImmediate(stagedTerrainObject);
            UnityEngine.Object.DestroyImmediate(stagedData);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void FailedReplayWorldPreparationPreservesRetainedWorld(bool failForest)
    {
        TerrainData terrainData = null;
        GameObject terrainObject = null;
        GameObject terrainGeneratorObject = null;
        GameObject forestObject = null;
        GameObject explorerObject = null;
        GameObject scriptsObject = null;
        GameObject uiObject = null;
        GameObject resultDrone = null;
        GameObject retainedTree = null;

        try
        {
            terrainData = new TerrainData { heightmapResolution = 33, size = new Vector3(64f, 12f, 48f) };
            var heights = new float[33, 33];
            heights[16, 16] = 0.625f;
            terrainData.SetHeights(0, 0, heights);
            terrainObject = Terrain.CreateTerrainGameObject(terrainData);
            Terrain terrain = terrainObject.GetComponent<Terrain>();

            terrainGeneratorObject = new GameObject("transaction-terrain-generator-test");
            Component terrainGenerator = terrainGeneratorObject.AddComponent(RequireType("TerrainGenerator, Assembly-CSharp"));
            SetField(terrainGenerator, "terrain", terrain);
            SetField(terrainGenerator, "widthx", 80);
            SetField(terrainGenerator, "widthz", 72);

            forestObject = new GameObject("transaction-forest-test");
            Component forest = forestObject.AddComponent(RequireType("ForestSpawner, Assembly-CSharp"));
            SetField(forest, "terrain", terrain);
            SetField(forest, "objectsPer100SquareMeters", failForest ? 1f : 0f);
            SetField(forest, "treePercent", 100f);
            SetField(forest, "treePrefab", null);
            retainedTree = new GameObject("retained-tree-transaction-test");
            retainedTree.transform.SetParent(forestObject.transform, false);
            retainedTree.transform.position = new Vector3(7f, 2f, 9f);
            ((IList)GetField(forest, "spawnedTreePositions")).Add(new Vector2(7f, 9f));

            explorerObject = new GameObject("retained-explorer-transaction-test");
            explorerObject.AddComponent<CharacterController>();
            Component explorer = explorerObject.AddComponent(RequireType("Explorer, Assembly-CSharp"));
            SetField(explorer, "terrain", terrain);
            SetField(explorer, "forestSpawner", forest);
            if (!failForest) SetField(explorer, "initialSpawnCheckRadius", 1000f);
            explorerObject.transform.position = new Vector3(13f, 3f, 17f);
            ((Behaviour)explorer).enabled = true;

            resultDrone = new GameObject("retained-drone-transaction-test");
            resultDrone.transform.position = new Vector3(23f, 5f, 29f);
            InvokePrivate(bootstrap, "RegisterSpawned", resultDrone);
            SetField(bootstrap, "gameplayStopped", true);
            SetField(bootstrap, "telemetryEnabled", false);

            uiObject = new GameObject("transaction-ui-test");
            Component ui = uiObject.AddComponent(RequireType("UiScriptsControl, Assembly-CSharp"));
            scriptsObject = new GameObject("transaction-scripts-test");
            Component scripts = scriptsObject.AddComponent(RequireType("ScriptsControl, Assembly-CSharp"));
            SetField(scripts, "terrainGenerator", terrainGenerator);
            SetField(scripts, "forestSpawner", forest);
            SetField(scripts, "droneSwarmDemoBootstrap", bootstrap);
            SetField(scripts, "explorer", explorer);
            SetField(scripts, "uiScriptsControl", ui);

            Vector3 oldSize = terrainData.size;
            float oldHeight = terrainData.GetHeight(16, 16);
            Vector3 oldTreePosition = retainedTree.transform.position;
            Vector3 oldExplorerPosition = explorerObject.transform.position;
            Vector3 oldDronePosition = resultDrone.transform.position;
            int terrainDataCountBeforeReplay = Resources.FindObjectsOfTypeAll<TerrainData>().Length;

            if (failForest)
            {
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("A tree prefab is required"));
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Forest generation failed; retained replay world"));
            }
            else
            {
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("too small for explorer clearance"));
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Explorer spawn failed; retained replay world"));
            }
            Assert.That((bool)InvokePublic(scripts, "TryStartNewSimulation"), Is.False);

            Assert.That(Resources.FindObjectsOfTypeAll<TerrainData>().Length, Is.EqualTo(terrainDataCountBeforeReplay),
                "Failed replay staging leaked its caller-owned TerrainData.");
            Assert.That(terrain.terrainData, Is.SameAs(terrainData));
            Assert.That(terrainData.size, Is.EqualTo(oldSize));
            Assert.That(terrainData.GetHeight(16, 16), Is.EqualTo(oldHeight));
            Assert.That(forestObject.transform.childCount, Is.EqualTo(1));
            Assert.That(((IList)GetField(forest, "spawnedTreePositions")).Count, Is.EqualTo(1));
            Assert.That(((IList)GetField(forest, "spawnedTreePositions"))[0], Is.EqualTo(new Vector2(7f, 9f)));
            Assert.That(retainedTree.activeSelf, Is.True);
            Assert.That(retainedTree.transform.position, Is.EqualTo(oldTreePosition));
            Assert.That(explorerObject.transform.position, Is.EqualTo(oldExplorerPosition));
            Assert.That(((Behaviour)explorer).enabled, Is.True);
            Assert.That(resultDrone.activeSelf, Is.True);
            Assert.That(resultDrone.transform.position, Is.EqualTo(oldDronePosition));
            Assert.That(GetProperty<bool>(bootstrap, "IsGameplayStopped"), Is.True);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(scriptsObject);
            UnityEngine.Object.DestroyImmediate(uiObject);
            UnityEngine.Object.DestroyImmediate(explorerObject);
            UnityEngine.Object.DestroyImmediate(forestObject);
            UnityEngine.Object.DestroyImmediate(terrainGeneratorObject);
            UnityEngine.Object.DestroyImmediate(terrainObject);
            UnityEngine.Object.DestroyImmediate(terrainData);
            UnityEngine.Object.DestroyImmediate(resultDrone);
        }
    }

    [TestCase("activation")]
    [TestCase("terrain")]
    [TestCase("forest")]
    [TestCase("explorer")]
    [TestCase("grid")]
    [TestCase("reset_queue")]
    [TestCase("reset_world")]
    [TestCase("reset_swarm")]
    [TestCase("reset_telemetry")]
    public void ReplayCommitFailureInjectionRestoresEveryRetainedObject(string failedStep)
    {
        TerrainData retainedData = null;
        GameObject terrainObject = null;
        GameObject generatorObject = null;
        GameObject forestObject = null;
        GameObject explorerObject = null;
        GameObject scriptsObject = null;
        GameObject uiObject = null;
        GameObject resultDrone = null;
        bool oldIgnoreLogs = LogAssert.ignoreFailingMessages;
        try
        {
            LogAssert.ignoreFailingMessages = true;
            retainedData = new TerrainData { heightmapResolution = 33, size = new Vector3(64f, 10f, 64f) };
            terrainObject = Terrain.CreateTerrainGameObject(retainedData);
            Terrain terrain = terrainObject.GetComponent<Terrain>();
            TerrainCollider collider = terrainObject.GetComponent<TerrainCollider>();

            generatorObject = new GameObject("injected-replay-generator");
            Component generator = generatorObject.AddComponent(RequireType("TerrainGenerator, Assembly-CSharp"));
            SetField(generator, "terrain", terrain);
            SetField(generator, "widthx", 64);
            SetField(generator, "widthz", 64);

            forestObject = new GameObject("injected-replay-forest");
            Component forest = forestObject.AddComponent(RequireType("ForestSpawner, Assembly-CSharp"));
            SetField(forest, "terrain", terrain);
            SetField(forest, "objectsPer100SquareMeters", 0f);
            var retainedTree = new GameObject("injected-retained-tree");
            retainedTree.transform.SetParent(forestObject.transform, false);
            retainedTree.transform.position = new Vector3(7f, 2f, 9f);
            ((IList)GetField(forest, "spawnedTreePositions")).Add(new Vector2(7f, 9f));

            explorerObject = new GameObject("injected-retained-explorer");
            CharacterController controller = explorerObject.AddComponent<CharacterController>();
            Component explorer = explorerObject.AddComponent(RequireType("Explorer, Assembly-CSharp"));
            SetField(explorer, "terrain", terrain);
            SetField(explorer, "forestSpawner", forest);
            explorerObject.transform.position = new Vector3(13f, 3f, 17f);
            ((Behaviour)explorer).enabled = false;
            controller.enabled = true;

            resultDrone = new GameObject("injected-retained-drone");
            resultDrone.transform.position = new Vector3(23f, 5f, 29f);
            InvokePrivate(bootstrap, "RegisterSpawned", resultDrone);
            SetField(bootstrap, "gameplayStopped", true);
            SetField(bootstrap, "missionComplete", true);
            SetField(bootstrap, "telemetryEnabled", false);

            uiObject = new GameObject("injected-replay-ui");
            Component ui = uiObject.AddComponent(RequireType("UiScriptsControl, Assembly-CSharp"));
            scriptsObject = new GameObject("injected-replay-scripts");
            Component scripts = scriptsObject.AddComponent(RequireType("ScriptsControl, Assembly-CSharp"));
            SetField(scripts, "terrainGenerator", generator);
            SetField(scripts, "forestSpawner", forest);
            SetField(scripts, "droneSwarmDemoBootstrap", bootstrap);
            SetField(scripts, "explorer", explorer);
            SetField(scripts, "uiScriptsControl", ui);
            SetField(scripts, "replayCommitExceptionInjection", (Action<string>)(step =>
            {
                if (step == failedStep) throw new InvalidOperationException("injected " + step);
            }));
            SetField(bootstrap, "resetRuntimeSetupExceptionInjection", (Action<string>)(step =>
            {
                if ("reset_" + step == failedStep) throw new InvalidOperationException("injected reset " + step);
            }));

            Vector3 treePosition = retainedTree.transform.position;
            Vector3 explorerPosition = explorerObject.transform.position;
            Vector3 dronePosition = resultDrone.transform.position;
            int terrainDataCount = Resources.FindObjectsOfTypeAll<TerrainData>().Length;

            bool resetRuntimeFailure = failedStep.StartsWith("reset_", StringComparison.Ordinal)
                && failedStep != "reset_queue";
            Assert.That((bool)InvokePublic(scripts, "TryStartNewSimulation"),
                Is.EqualTo(resetRuntimeFailure));
            if (resetRuntimeFailure)
            {
                IEnumerator reset = (IEnumerator)InvokePrivate(bootstrap, "ResetDemoNextFrame");
                while (reset.MoveNext()) { }
                Assert.That(GetProperty<bool>(bootstrap, "LastResetSucceeded"), Is.False);
            }

            Assert.That(terrain.terrainData, Is.SameAs(retainedData));
            Assert.That(collider.terrainData, Is.SameAs(retainedData));
            Assert.That(Resources.FindObjectsOfTypeAll<TerrainData>().Length, Is.EqualTo(terrainDataCount));
            Assert.That(forestObject.transform.childCount, Is.EqualTo(1));
            Assert.That(retainedTree.transform.parent, Is.SameAs(forestObject.transform));
            Assert.That(retainedTree.activeSelf, Is.True);
            Assert.That(retainedTree.transform.position, Is.EqualTo(treePosition));
            Assert.That(((IList)GetField(forest, "spawnedTreePositions"))[0], Is.EqualTo(new Vector2(7f, 9f)));
            Assert.That(explorerObject.transform.position, Is.EqualTo(explorerPosition));
            Assert.That(((Behaviour)explorer).enabled, Is.False);
            Assert.That(controller.enabled, Is.True);
            Assert.That(explorer.GetType().GetField("terrain").GetValue(explorer), Is.SameAs(terrain));
            Assert.That(explorer.GetType().GetField("forestSpawner").GetValue(explorer), Is.SameAs(forest));
            Assert.That(resultDrone.activeSelf, Is.True);
            Assert.That(resultDrone.transform.position, Is.EqualTo(dronePosition));
            Assert.That(GetProperty<bool>(bootstrap, "IsGameplayStopped"), Is.True);
            Assert.That(GetProperty<TerrainData>(generator, "OwnedRuntimeTerrainData"), Is.Null);
        }
        finally
        {
            LogAssert.ignoreFailingMessages = oldIgnoreLogs;
            UnityEngine.Object.DestroyImmediate(scriptsObject);
            UnityEngine.Object.DestroyImmediate(uiObject);
            UnityEngine.Object.DestroyImmediate(explorerObject);
            UnityEngine.Object.DestroyImmediate(forestObject);
            UnityEngine.Object.DestroyImmediate(generatorObject);
            UnityEngine.Object.DestroyImmediate(terrainObject);
            UnityEngine.Object.DestroyImmediate(retainedData);
            UnityEngine.Object.DestroyImmediate(resultDrone);
        }
    }

    [Test]
    public void NewGameBoundaryRejectsAnUnstoppedSimulation()
    {
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Replay can only begin after"));

        Assert.That((bool)InvokePublic(bootstrap, "TryPrepareForNewGame"), Is.False);
        Assert.That(GetProperty<bool>(bootstrap, "IsGameplayStopped"), Is.False);
    }

    [Test]
    public void DisablingBootstrapStopsGameplayAndEndsTelemetryWithLifecycleReason()
    {
        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", Activator.CreateInstance(configType)), Is.True);

        ((Behaviour)bootstrap).enabled = false;
        InvokePrivate(bootstrap, "OnDisable");

        Assert.That(GetProperty<bool>(bootstrap, "IsGameplayStopped"), Is.True);
        Assert.That(GetProperty<bool>(recorder, "HasActiveSession"), Is.False);
        string[] rows = File.ReadAllLines(GetProperty<string>(recorder, "SummaryFilePath"));
        Assert.That(rows.Count(line => line.Contains(",bootstrap_disabled,")), Is.EqualTo(1));
    }

    [Test]
    public void DisableDoesNotReplaceExplicitStopReason()
    {
        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", Activator.CreateInstance(configType)), Is.True);
        InvokePublic(bootstrap, "StopSimulation", "forced", false);

        ((Behaviour)bootstrap).enabled = false;
        InvokePrivate(bootstrap, "OnDisable");

        string[] rows = File.ReadAllLines(GetProperty<string>(recorder, "SummaryFilePath"));
        Assert.That(rows.Count(line => line.Contains(",forced,")), Is.EqualTo(1));
        Assert.That(rows.Any(line => line.Contains(",bootstrap_disabled,")), Is.False);
    }

    [Test]
    public void DisableLeavesFailedEndRecoverableWithoutStartingCoroutine()
    {
        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", Activator.CreateInstance(configType)), Is.True);
        string summaryPath = GetProperty<string>(recorder, "SummaryFilePath");
        string originalSummary = File.ReadAllText(summaryPath);
        File.Delete(summaryPath);
        Directory.CreateDirectory(summaryPath);

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Session end remains pending"));
        ((Behaviour)bootstrap).enabled = false;
        InvokePrivate(bootstrap, "OnDisable");

        Assert.That(GetField(bootstrap, "finalTelemetryRetryCoroutine"), Is.Null);
        Assert.That(GetProperty<bool>(recorder, "HasActiveSession"), Is.True);
        string[] pending = (string[])GetField(recorder, "pendingSummaryRow");
        Assert.That(pending, Does.Contain("bootstrap_disabled"));

        Directory.Delete(summaryPath, true);
        File.WriteAllText(summaryPath, originalSummary);
        Assert.That((bool)InvokePublic(bootstrap, "EndActiveTelemetrySession", "later_cleanup", false), Is.True);
        Assert.That(File.ReadAllLines(summaryPath).Count(line => line.Contains(",bootstrap_disabled,")), Is.EqualTo(1));
    }

    [Test]
    public void DisableCancelsQueuedReset()
    {
        SetField(bootstrap, "resetQueued", true);

        ((Behaviour)bootstrap).enabled = false;
        InvokePrivate(bootstrap, "OnDisable");

        Assert.That(GetProperty<bool>(bootstrap, "IsResetQueued"), Is.False);
        Assert.That(GetProperty<bool>(bootstrap, "LastResetSucceeded"), Is.False);
        Assert.That(GetProperty<string>(bootstrap, "LastResetError"), Does.Contain("disabled"));
    }

    [Test]
    public void InitialQueuedResetDisableKeepsInitialBoundaryAndReenabledResetSucceeds()
    {
        SetField(bootstrap, "telemetryEnabled", false);
        SetField(bootstrap, "width", 4);
        SetField(bootstrap, "depth", 4);
        SetField(bootstrap, "droneCount", 1);

        var scriptsObject = new GameObject("disabled-initial-start-scripts-test");
        Component scripts = scriptsObject.AddComponent(RequireType("ScriptsControl, Assembly-CSharp"));
        SetField(scripts, "droneSwarmDemoBootstrap", bootstrap);
        PreparePendingSetup(scripts, "Initial", replayActivated: false, initialRollbackRequired: true);
        Action<bool, string> resetHandler = (Action<bool, string>)Delegate.CreateDelegate(
            typeof(Action<bool, string>),
            scripts,
            scripts.GetType().GetMethod("OnResetCompleted", BindingFlags.Instance | BindingFlags.NonPublic));
        EventInfo resetCompleted = bootstrap.GetType().GetEvent("ResetCompleted", BindingFlags.Instance | BindingFlags.Public);
        resetCompleted.AddEventHandler(bootstrap, resetHandler);

        bool previousIgnore = LogAssert.ignoreFailingMessages;
        try
        {
            SetField(bootstrap, "resetQueued", true);
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("disabled"));
            LogAssert.ignoreFailingMessages = true;

            ((Behaviour)bootstrap).enabled = false;
            if (GetProperty<bool>(bootstrap, "IsResetQueued"))
            {
                // EditMode does not consistently dispatch MonoBehaviour lifecycle callbacks.
                InvokePrivate(bootstrap, "OnDisable");
            }

            Assert.That(GetProperty<bool>(bootstrap, "IsResetQueued"), Is.False);
            Assert.That(GetProperty<bool>(bootstrap, "IsGameplayStopped"), Is.False,
                "initial cancellation completion must remain authoritative after lifecycle cleanup");
            Assert.That(GetProperty<bool>(scripts, "IsSetupInProgress"), Is.False);
            Assert.That(GetProperty<bool>(scripts, "IsStarted"), Is.False);

            ((Behaviour)bootstrap).enabled = true;
            Assert.That((bool)InvokePublic(bootstrap, "TryResetDemo"), Is.True,
                "re-enabling after an initial cancellation must permit another initial reset");
            Coroutine queuedCoroutine = (Coroutine)GetField(bootstrap, "resetCoroutine");
            if (queuedCoroutine != null)
            {
                ((MonoBehaviour)bootstrap).StopCoroutine(queuedCoroutine);
                SetField(bootstrap, "resetCoroutine", null);
            }

            IEnumerator retry = (IEnumerator)InvokePrivate(bootstrap, "ResetDemoNextFrame");
            while (retry.MoveNext()) { }

            Assert.That(GetProperty<bool>(bootstrap, "LastResetSucceeded"), Is.True);
            Assert.That(GetProperty<bool>(bootstrap, "IsGameplayStopped"), Is.False);
        }
        finally
        {
            LogAssert.ignoreFailingMessages = previousIgnore;
            resetCompleted.RemoveEventHandler(bootstrap, resetHandler);
            UnityEngine.Object.DestroyImmediate(scriptsObject);
            DestroySpawnedBootstrapObjects();
        }
    }

    [Test]
    public void CancelCleanupDuringQueuedResetCancelsBootstrapWorkAndPreservesReason()
    {
        InvokePublic(bootstrap, "SetTelemetryBatchContext", "cancel-reset-test", 1, 1, 1, true, 17);
        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", Activator.CreateInstance(configType)), Is.True);
        SetField(bootstrap, "resetQueued", true);

        InvokePrivate(runner, "CleanupBatch", "batch_cancelled");

        Assert.That(GetProperty<bool>(bootstrap, "IsResetQueued"), Is.False);
        Assert.That(GetProperty<bool>(bootstrap, "LastResetSucceeded"), Is.False);
        Assert.That(GetProperty<string>(bootstrap, "LastResetError"), Does.Contain("batch_cancelled"));
        Assert.That(GetProperty<bool>(bootstrap, "IsGameplayStopped"), Is.False,
            "batch shutdown must not permanently block a later intentional reset");
        Assert.That(GetProperty<bool>(recorder, "HasActiveSession"), Is.False);
        Assert.That(File.ReadAllLines(GetProperty<string>(recorder, "SummaryFilePath"))
            .Count(line => line.Contains(",batch_cancelled,")), Is.EqualTo(1));
    }

    [Test]
    public void TimedCancelCleanupAlsoCancelsQueuedReset()
    {
        var timedRunnerObject = new GameObject("timed-batch-cancel-reset-test");
        Component timedRunner = timedRunnerObject.AddComponent(RequireType("DroneMissionTimedBatchRunner, Assembly-CSharp"));
        SetField(timedRunner, "bootstrap", bootstrap);
        SetField(bootstrap, "resetQueued", true);

        try
        {
            InvokePrivate(timedRunner, "CleanupBatch", "batch_cancelled");
            Assert.That(GetProperty<bool>(bootstrap, "IsResetQueued"), Is.False);
            Assert.That(GetProperty<string>(bootstrap, "LastResetError"), Does.Contain("batch_cancelled"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(timedRunnerObject);
        }
    }

    [Test]
    public void SetupFailureCleanupEndsOnceAndClearsBatchContext()
    {
        InvokePublic(bootstrap, "SetTelemetryBatchContext", "batch-test", 4, 2, 1, true, 123);
        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", Activator.CreateInstance(configType)), Is.True);

        InvokePrivate(runner, "CleanupBatch", "setup_failed");

        Assert.That(GetProperty<bool>(recorder, "HasActiveSession"), Is.False);
        Assert.That((string)GetField(bootstrap, "telemetryBatchId"), Is.Empty);
        Assert.That((bool)GetField(bootstrap, "telemetryHasBatchRunIndex"), Is.False);

        string summaryPath = GetProperty<string>(recorder, "SummaryFilePath");
        string[] rows = File.ReadAllLines(summaryPath);
        Assert.That(rows.Count(line => line.Contains(",setup_failed,")), Is.EqualTo(1));

        InvokePrivate(runner, "CleanupBatch", "batch_cancelled");
        Assert.That(File.ReadAllLines(summaryPath), Has.Length.EqualTo(rows.Length));
    }

    [Test]
    public void CleanupRetriesButDoesNotReplacePendingEndRows()
    {
        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", Activator.CreateInstance(configType)), Is.True);
        string summaryPath = GetProperty<string>(recorder, "SummaryFilePath");
        File.Delete(summaryPath);
        Directory.CreateDirectory(summaryPath);

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        Assert.That((bool)InvokePublic(recorder, "TryEndSession", "mission_complete", true), Is.False);
        string[] pendingBefore = (string[])GetField(recorder, "pendingSummaryRow");

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Session end remains pending"));
        InvokePrivate(runner, "CleanupBatch", "telemetry_write_failed");

        CollectionAssert.AreEqual(pendingBefore, (string[])GetField(recorder, "pendingSummaryRow"));
        Assert.That(pendingBefore, Does.Contain("mission_complete"));
        Assert.That(pendingBefore, Does.Not.Contain("telemetry_write_failed"));

        // Idempotent cleanup must not attempt a second end or emit another write error.
        InvokePrivate(runner, "CleanupBatch", "batch_cancelled");
        Assert.That((bool)GetField(runner, "batchCleanupCompleted"), Is.False,
            "cleanup cannot complete while bounded final-row recovery is still pending");
    }

    [Test]
    public void BootstrapDisableAndReenableResumesPendingFinalWriteWithoutStoppedGameplayLatch()
    {
        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", Activator.CreateInstance(configType)), Is.True);
        string summaryPath = GetProperty<string>(recorder, "SummaryFilePath");
        string originalSummary = File.ReadAllText(summaryPath);
        File.Delete(summaryPath);
        Directory.CreateDirectory(summaryPath);

        bool previousIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            InvokePrivate(bootstrap, "CompleteNaturalMission");
            InvokePublic(bootstrap, "SetTelemetryBatchContext", "bootstrap-resume-test", 1, 1, 1, true, 7);
            Assert.That(GetProperty<bool>(bootstrap, "IsGameplayStopped"), Is.False);
            Assert.That(GetProperty<bool>(bootstrap, "IsFinalTelemetryPersistencePending"), Is.True);

            ((Behaviour)bootstrap).enabled = false;
            InvokePrivate(bootstrap, "OnDisable");
            Assert.That(GetProperty<bool>(bootstrap, "IsGameplayStopped"), Is.False,
                "batch lifecycle shutdown must not set the interactive stopped latch");
            Assert.That(GetField(bootstrap, "finalTelemetryRetryCoroutine"), Is.Null);
            Assert.That((bool)GetField(bootstrap, "finalTelemetryPersistencePending"), Is.True);

            Directory.Delete(summaryPath, true);
            File.WriteAllText(summaryPath, originalSummary);
            ((Behaviour)bootstrap).enabled = true;
            InvokePrivate(bootstrap, "OnEnable");
            Assert.That(GetField(bootstrap, "finalTelemetryRetryCoroutine"), Is.Not.Null,
                "pending final persistence must resume even when gameplayStopped is false");

            ((MonoBehaviour)bootstrap).StopCoroutine((Coroutine)GetField(bootstrap, "finalTelemetryRetryCoroutine"));
            SetField(bootstrap, "finalTelemetryRetryCoroutine", null);
            IEnumerator retry = (IEnumerator)InvokePrivate(bootstrap, "RetryFinalTelemetryPersistence");
            Assert.That(retry.MoveNext(), Is.True);
            Assert.That(retry.MoveNext(), Is.False);

            Assert.That(GetProperty<bool>(bootstrap, "IsFinalTelemetryPersistencePending"), Is.False);
            Assert.That(File.ReadAllLines(summaryPath)
                .Count(line => line.Contains(",mission_complete,")), Is.EqualTo(1));
        }
        finally
        {
            LogAssert.ignoreFailingMessages = previousIgnore;
        }
    }

    [TestCase("DroneMissionBatchRunner, Assembly-CSharp")]
    [TestCase("DroneMissionTimedBatchRunner, Assembly-CSharp")]
    public void RunnerDisableAndReenableResumesPendingCleanup(string runnerAssemblyName)
    {
        GameObject testedObject = runnerObject;
        Component testedRunner = runner;
        if (!runner.GetType().AssemblyQualifiedName.StartsWith(runnerAssemblyName.Split(',')[0], StringComparison.Ordinal))
        {
            testedObject = new GameObject("pending-cleanup-timed-runner-test");
            testedRunner = testedObject.AddComponent(RequireType(runnerAssemblyName));
            SetField(testedRunner, "bootstrap", bootstrap);
            SetField(testedRunner, "telemetryRecorder", recorder);
        }

        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", Activator.CreateInstance(configType)), Is.True);
        InvokePublic(bootstrap, "SetTelemetryBatchContext", "pending-cleanup-test", 1, 1, 1, true, 41);
        string summaryPath = GetProperty<string>(recorder, "SummaryFilePath");
        string originalSummary = File.ReadAllText(summaryPath);
        File.Delete(summaryPath);
        Directory.CreateDirectory(summaryPath);

        bool previousIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            InvokePrivate(testedRunner, "CleanupBatch", "batch_stopped");
            Assert.That((bool)GetField(testedRunner, "telemetryCleanupPending"), Is.True);
            Assert.That(GetProperty<bool>(testedRunner, "IsRunning"), Is.True);
            Assert.That(GetField(testedRunner, "telemetryCleanupCoroutine"), Is.Not.Null);

            ((Behaviour)testedRunner).enabled = false;
            InvokePrivate(testedRunner, "OnDisable");
            Assert.That(GetField(testedRunner, "telemetryCleanupCoroutine"), Is.Null,
                "a stopped Unity coroutine handle must not remain live-looking");
            Assert.That((bool)GetField(testedRunner, "telemetryCleanupPending"), Is.True);
            Assert.That(GetProperty<bool>(testedRunner, "IsRunning"), Is.True);
            Assert.That((string)GetField(bootstrap, "telemetryBatchId"), Is.EqualTo("pending-cleanup-test"));

            Directory.Delete(summaryPath, true);
            File.WriteAllText(summaryPath, originalSummary);
            Assert.That((bool)InvokePublic(bootstrap, "EndActiveTelemetrySession", "external_recovery", false), Is.True);

            ((Behaviour)testedRunner).enabled = true;
            InvokePrivate(testedRunner, "OnEnable");
            Assert.That((bool)GetField(testedRunner, "batchCleanupCompleted"), Is.True);
            Assert.That((bool)GetField(testedRunner, "telemetryCleanupPending"), Is.False);
            Assert.That(GetProperty<bool>(testedRunner, "IsRunning"), Is.False);
            Assert.That((string)GetField(bootstrap, "telemetryBatchId"), Is.Empty);
        }
        finally
        {
            LogAssert.ignoreFailingMessages = previousIgnore;
            if (testedObject != runnerObject)
            {
                UnityEngine.Object.DestroyImmediate(testedObject);
            }
        }
    }

    [Test]
    public void NaturalCompletionRetriesTransientFinalWriteWithoutDuplicateRows()
    {
        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", Activator.CreateInstance(configType)), Is.True);
        string summaryPath = GetProperty<string>(recorder, "SummaryFilePath");
        string originalSummary = File.ReadAllText(summaryPath);
        File.Delete(summaryPath);
        Directory.CreateDirectory(summaryPath);

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Session end remains pending"));
        InvokePrivate(bootstrap, "CompleteNaturalMission");

        Assert.That(GetProperty<bool>(bootstrap, "MissionComplete"), Is.True);
        Assert.That(GetProperty<bool>(bootstrap, "IsFinalTelemetryPersistencePending"), Is.True);
        Assert.That(GetField(bootstrap, "finalTelemetryRetryCoroutine"), Is.Not.Null);

        // Drive the realtime retry deterministically rather than waiting in EditMode.
        ((MonoBehaviour)bootstrap).StopCoroutine((Coroutine)GetField(bootstrap, "finalTelemetryRetryCoroutine"));
        SetField(bootstrap, "finalTelemetryRetryCoroutine", null);
        Directory.Delete(summaryPath, true);
        File.WriteAllText(summaryPath, originalSummary);

        IEnumerator retry = (IEnumerator)InvokePrivate(bootstrap, "RetryFinalTelemetryPersistence");
        Assert.That(retry.MoveNext(), Is.True);
        Assert.That(retry.MoveNext(), Is.False);

        Assert.That(GetProperty<bool>(recorder, "HasActiveSession"), Is.False);
        Assert.That(GetProperty<bool>(recorder, "HasPersistenceFailure"), Is.False);
        Assert.That(GetProperty<bool>(bootstrap, "IsFinalTelemetryPersistencePending"), Is.False);
        string eventPath = Path.Combine(outputDirectory, "session_events.csv");
        Assert.That(File.ReadAllLines(eventPath)
            .Count(line => line.Contains(",mission_complete,")), Is.EqualTo(1));
        Assert.That(File.ReadAllLines(eventPath)
            .Count(line => line.Contains(",session_end,")), Is.EqualTo(1));
        Assert.That(File.ReadAllLines(summaryPath)
            .Count(line => line.Contains(",mission_complete,")), Is.EqualTo(1));
    }

    [Test]
    public void ExpiredRunHardDeadlineEndsActiveSessionOnceWithBatchTimeoutReason()
    {
        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", Activator.CreateInstance(configType)), Is.True);
        var activeDroneObject = new GameObject("fixed-batch-active-drone-test");
        Component activeDrone = activeDroneObject.AddComponent(RequireType("DroneFrontierExplorer, Assembly-CSharp"));
        Component commandPlanner = activeDroneObject.AddComponent(RequireType("DroneCommandRoutePlanner, Assembly-CSharp"));
        ((IList)GetField(bootstrap, "explorers")).Add(activeDrone);
        SetField(bootstrap, "commandRoutePlanner", commandPlanner);
        var activeHumanObject = new GameObject("fixed-batch-active-human-test");
        activeHumanObject.AddComponent<CharacterController>();
        Component activeHuman = activeHumanObject.AddComponent(RequireType("Explorer, Assembly-CSharp"));
        SetField(bootstrap, "humanExplorer", activeHuman);

        try
        {
            IEnumerator wait = (IEnumerator)InvokePrivate(
                runner,
                "WaitForActiveSessionToEnd",
                "batch-timeout-test",
                1,
                Time.realtimeSinceStartup - 1f);
            Assert.That(wait.MoveNext(), Is.False);

            Assert.That(GetProperty<bool>(recorder, "HasActiveSession"), Is.False);
            Assert.That((bool)GetField(runner, "runHardTimeoutReached"), Is.True);
            Assert.That(((Behaviour)activeDrone).enabled, Is.False);
            Assert.That(((Behaviour)activeDroneObject.GetComponent(RequireType("DroneGridSensor, Assembly-CSharp"))).enabled, Is.False);
            Assert.That(((Behaviour)activeDroneObject.GetComponent(RequireType("DronePathFollower, Assembly-CSharp"))).enabled, Is.False);
            Assert.That(((Behaviour)commandPlanner).enabled, Is.False);
            Assert.That(((Behaviour)activeHuman).enabled, Is.False);
            string summaryPath = GetProperty<string>(recorder, "SummaryFilePath");
            string[] rows = File.ReadAllLines(summaryPath);
            Assert.That(rows.Count(line => line.Contains(",batch_timeout,")), Is.EqualTo(1));

            InvokePrivate(runner, "CleanupBatch", "batch_timeout");
            Assert.That(File.ReadAllLines(summaryPath), Has.Length.EqualTo(rows.Length));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(activeHumanObject);
            UnityEngine.Object.DestroyImmediate(activeDroneObject);
        }
    }

    [TestCase("world")]
    [TestCase("swarm")]
    [TestCase("telemetry")]
    public void ResetSetupExceptionCompletesFailureOnceAndClearsPartialRuntime(string injectedStage)
    {
        SetField(bootstrap, "telemetryEnabled", false);
        SetField(bootstrap, "width", 4);
        SetField(bootstrap, "depth", 4);
        SetField(bootstrap, "droneCount", 1);
        SetField(bootstrap, "resetQueued", true);
        SetField(bootstrap, "resetRuntimeSetupExceptionInjection", (Action<string>)(stage =>
        {
            if (stage == injectedStage)
            {
                throw new InvalidOperationException($"injected {stage} reset failure");
            }
        }));
        var partial = new GameObject("reset-exception-partial-runtime-test");
        InvokePrivate(bootstrap, "RegisterSpawned", partial);

        int completionCount = 0;
        Action<bool, string> handler = (succeeded, error) =>
        {
            completionCount++;
            Assert.That(succeeded, Is.False);
            Assert.That(error, Does.Contain($"injected {injectedStage} reset failure"));
        };
        EventInfo resetCompleted = bootstrap.GetType().GetEvent("ResetCompleted", BindingFlags.Instance | BindingFlags.Public);
        resetCompleted.AddEventHandler(bootstrap, handler);

        bool previousIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            IEnumerator reset = (IEnumerator)InvokePrivate(bootstrap, "ResetDemoNextFrame");
            while (reset.MoveNext()) { }

            Assert.That(completionCount, Is.EqualTo(1));
            Assert.That(GetProperty<bool>(bootstrap, "IsResetQueued"), Is.False);
            Assert.That(GetProperty<bool>(bootstrap, "LastResetSucceeded"), Is.False);
            Assert.That(GetProperty<bool>(bootstrap, "MissionComplete"), Is.True);
            Assert.That(((IList)GetField(bootstrap, "spawnedObjects")).Count, Is.Zero);
        }
        finally
        {
            LogAssert.ignoreFailingMessages = previousIgnore;
            resetCompleted.RemoveEventHandler(bootstrap, handler);
            SetField(bootstrap, "resetRuntimeSetupExceptionInjection", null);
            if (partial != null) UnityEngine.Object.DestroyImmediate(partial);
            DestroySpawnedBootstrapObjects();
        }
    }

    [Test]
    public void FixedRunnerExpiredSetupDeadlineCancelsQueuedResetWithTimeoutReason()
    {
        SetField(bootstrap, "telemetryEnabled", false);
        SetField(bootstrap, "resetQueued", true);

        Assert.That((bool)InvokePrivate(
            runner,
            "TryReachRunHardDeadline",
            Time.realtimeSinceStartup - 1f,
            "fixed-setup-deadline-test",
            1), Is.True);

        Assert.That((bool)GetField(runner, "runHardTimeoutReached"), Is.True);
        Assert.That(GetProperty<bool>(bootstrap, "IsResetQueued"), Is.False);
        Assert.That(GetProperty<string>(bootstrap, "LastResetError"), Does.Contain("batch_timeout"));
    }

    [Test]
    public void TimedRunnerExpiredSetupDeadlineCancelsQueuedResetWithRunHardReason()
    {
        var timedRunnerObject = new GameObject("timed-setup-deadline-test");
        Component timedRunner = timedRunnerObject.AddComponent(RequireType("DroneMissionTimedBatchRunner, Assembly-CSharp"));
        SetField(timedRunner, "bootstrap", bootstrap);
        SetField(timedRunner, "telemetryRecorder", recorder);
        SetField(bootstrap, "telemetryEnabled", false);
        SetField(bootstrap, "resetQueued", true);

        try
        {
            Assert.That((bool)InvokePrivate(
                timedRunner,
                "TryReachRunHardDeadline",
                Time.realtimeSinceStartup - 1f,
                "timed-setup-deadline-test",
                1), Is.True);

            Assert.That(GetProperty<bool>(bootstrap, "IsResetQueued"), Is.False);
            Assert.That(GetProperty<string>(bootstrap, "LastResetError"), Does.Contain("run_hard_timeout"));
            Assert.That((bool)GetField(timedRunner, "wallClockTimeoutReached"), Is.False);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(timedRunnerObject);
        }
    }

    [Test]
    public void QueuedResetReportsSuccessOnlyAfterDelayedWorkCompletes()
    {
        SetField(bootstrap, "telemetryEnabled", false);
        SetField(bootstrap, "width", 4);
        SetField(bootstrap, "depth", 4);
        SetField(bootstrap, "droneCount", 1);
        SetField(bootstrap, "resetQueued", true);

        bool completionCalled = false;
        bool completionSucceeded = false;
        Action<bool, string> handler = (succeeded, _) =>
        {
            completionCalled = true;
            completionSucceeded = succeeded;
        };
        EventInfo resetCompleted = bootstrap.GetType().GetEvent("ResetCompleted", BindingFlags.Instance | BindingFlags.Public);
        Assert.That(resetCompleted, Is.Not.Null);
        resetCompleted.AddEventHandler(bootstrap, handler);

        bool previousIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            IEnumerator reset = (IEnumerator)InvokePrivate(bootstrap, "ResetDemoNextFrame");
            Assert.That(reset.MoveNext(), Is.True, "reset must yield before rebuilding the world");
            Assert.That(completionCalled, Is.False);
            Assert.That(GetProperty<bool>(bootstrap, "LastResetSucceeded"), Is.False);
            Assert.That(GetProperty<bool>(bootstrap, "IsResetQueued"), Is.True);

            while (reset.MoveNext())
            {
            }

            Assert.That(completionCalled, Is.True);
            Assert.That(completionSucceeded, Is.True);
            Assert.That(GetProperty<bool>(bootstrap, "LastResetSucceeded"), Is.True);
        }
        finally
        {
            LogAssert.ignoreFailingMessages = previousIgnore;
            resetCompleted.RemoveEventHandler(bootstrap, handler);
            DestroySpawnedBootstrapObjects();
        }
    }

    [Test]
    public void CancelledReplayResetRestoresStoppedBoundaryAndRetrySucceeds()
    {
        SetField(bootstrap, "telemetryEnabled", false);
        SetField(bootstrap, "width", 4);
        SetField(bootstrap, "depth", 4);
        SetField(bootstrap, "droneCount", 1);
        SetField(bootstrap, "gameplayStopped", true);

        var scriptsObject = new GameObject("cancelled-replay-scripts-test");
        Component scripts = scriptsObject.AddComponent(RequireType("ScriptsControl, Assembly-CSharp"));
        SetField(scripts, "droneSwarmDemoBootstrap", bootstrap);
        PreparePendingSetup(scripts, "Replay", replayActivated: true, initialRollbackRequired: false);
        Action<bool, string> resetHandler = (Action<bool, string>)Delegate.CreateDelegate(
            typeof(Action<bool, string>),
            scripts,
            scripts.GetType().GetMethod("OnResetCompleted", BindingFlags.Instance | BindingFlags.NonPublic));
        EventInfo resetCompleted = bootstrap.GetType().GetEvent("ResetCompleted", BindingFlags.Instance | BindingFlags.Public);
        resetCompleted.AddEventHandler(bootstrap, resetHandler);

        bool previousIgnore = LogAssert.ignoreFailingMessages;
        try
        {
            SetField(bootstrap, "gameplayStopped", false);
            SetField(bootstrap, "resetQueued", true);
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("disabled"));
            LogAssert.ignoreFailingMessages = true;

            ((Behaviour)bootstrap).enabled = false;
            if (GetProperty<bool>(bootstrap, "IsResetQueued"))
            {
                InvokePrivate(bootstrap, "OnDisable");
            }

            Assert.That(GetProperty<bool>(bootstrap, "IsGameplayStopped"), Is.True,
                "replay cancellation must restore its stopped new-game boundary");
            ((Behaviour)bootstrap).enabled = true;
            Assert.That((bool)InvokePublic(bootstrap, "TryPrepareForNewGame"), Is.True);
            Assert.That((bool)InvokePublic(bootstrap, "TryActivatePreparedNewGame"), Is.True);

            SetField(bootstrap, "resetQueued", true);
            IEnumerator retry = (IEnumerator)InvokePrivate(bootstrap, "ResetDemoNextFrame");
            while (retry.MoveNext()) { }

            Assert.That(GetProperty<bool>(bootstrap, "LastResetSucceeded"), Is.True);
            Assert.That(GetProperty<bool>(bootstrap, "IsGameplayStopped"), Is.False);
        }
        finally
        {
            LogAssert.ignoreFailingMessages = previousIgnore;
            resetCompleted.RemoveEventHandler(bootstrap, resetHandler);
            UnityEngine.Object.DestroyImmediate(scriptsObject);
            DestroySpawnedBootstrapObjects();
        }
    }

    [Test]
    public void FailedReplayTelemetryStartRestoresStoppedBoundaryAndRetrySucceeds()
    {
        SetField(bootstrap, "width", 4);
        SetField(bootstrap, "depth", 4);
        SetField(bootstrap, "droneCount", 1);
        string blockedPath = Path.Combine(Application.persistentDataPath, "DroneTelemetryTests", Guid.NewGuid().ToString("N"), "blocked");
        Directory.CreateDirectory(Path.GetDirectoryName(blockedPath));
        File.WriteAllText(blockedPath, "not-a-directory");
        SetField(recorder, "outputDirectoryName", blockedPath);

        var scriptsObject = new GameObject("failed-replay-scripts-test");
        Component scripts = scriptsObject.AddComponent(RequireType("ScriptsControl, Assembly-CSharp"));
        SetField(scripts, "droneSwarmDemoBootstrap", bootstrap);
        PreparePendingSetup(scripts, "Replay", replayActivated: true, initialRollbackRequired: false);
        Action<bool, string> resetHandler = (Action<bool, string>)Delegate.CreateDelegate(
            typeof(Action<bool, string>),
            scripts,
            scripts.GetType().GetMethod("OnResetCompleted", BindingFlags.Instance | BindingFlags.NonPublic));
        EventInfo resetCompleted = bootstrap.GetType().GetEvent("ResetCompleted", BindingFlags.Instance | BindingFlags.Public);
        resetCompleted.AddEventHandler(bootstrap, resetHandler);

        bool previousIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            SetField(bootstrap, "resetQueued", true);
            IEnumerator failed = (IEnumerator)InvokePrivate(bootstrap, "ResetDemoNextFrame");
            while (failed.MoveNext()) { }

            Assert.That(GetProperty<bool>(bootstrap, "LastResetSucceeded"), Is.False);
            Assert.That(GetProperty<bool>(bootstrap, "IsGameplayStopped"), Is.True);

            File.Delete(blockedPath);
            SetField(recorder, "outputDirectoryName", Path.Combine("DroneTelemetryTests", Guid.NewGuid().ToString("N")));
            Assert.That((bool)InvokePublic(bootstrap, "TryPrepareForNewGame"), Is.True);
            Assert.That((bool)InvokePublic(bootstrap, "TryActivatePreparedNewGame"), Is.True);
            SetField(bootstrap, "resetQueued", true);
            IEnumerator retry = (IEnumerator)InvokePrivate(bootstrap, "ResetDemoNextFrame");
            while (retry.MoveNext()) { }

            Assert.That(GetProperty<bool>(bootstrap, "LastResetSucceeded"), Is.True);
            Assert.That(GetProperty<bool>(bootstrap, "IsGameplayStopped"), Is.False);
        }
        finally
        {
            LogAssert.ignoreFailingMessages = previousIgnore;
            resetCompleted.RemoveEventHandler(bootstrap, resetHandler);
            UnityEngine.Object.DestroyImmediate(scriptsObject);
            DestroySpawnedBootstrapObjects();
            if (File.Exists(blockedPath)) File.Delete(blockedPath);
            string parent = Path.GetDirectoryName(blockedPath);
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [Test]
    public void InitialTelemetryStartFailureClearsGeneratedForestBeforeRetry()
    {
        TerrainData terrainData = new TerrainData { heightmapResolution = 33, size = new Vector3(20f, 5f, 20f) };
        GameObject terrainObject = Terrain.CreateTerrainGameObject(terrainData);
        GameObject forestObject = new GameObject("initial-rollback-forest-test");
        Component forest = forestObject.AddComponent(RequireType("ForestSpawner, Assembly-CSharp"));
        SetField(forest, "terrain", terrainObject.GetComponent<Terrain>());
        SetField(forest, "objectsPer100SquareMeters", 1f);
        SetField(forest, "treePercent", 100f);
        SetField(forest, "minHeight", -1f);
        SetField(forest, "minDistance", 0f);
        GameObject treePrefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        SetField(forest, "treePrefab", treePrefab);
        GameObject explorerObject = new GameObject("initial-rollback-explorer-test");
        explorerObject.AddComponent<CharacterController>();
        Component explorer = explorerObject.AddComponent(RequireType("Explorer, Assembly-CSharp"));
        var scriptsObject = new GameObject("initial-rollback-scripts-test");
        Component scripts = scriptsObject.AddComponent(RequireType("ScriptsControl, Assembly-CSharp"));
        SetField(scripts, "forestSpawner", forest);
        SetField(scripts, "explorer", explorer);
        SetField(scripts, "droneSwarmDemoBootstrap", bootstrap);
        PreparePendingSetup(scripts, "Initial", replayActivated: false, initialRollbackRequired: true);

        for (int i = 0; i < 4; i++)
        {
            var generated = new GameObject($"failed-initial-tree-{i}");
            generated.transform.SetParent(forestObject.transform, false);
            ((IList)GetField(forest, "spawnedTreePositions")).Add(new Vector2(i, i));
        }

        string blockedPath = Path.Combine(Application.persistentDataPath, "DroneTelemetryTests", Guid.NewGuid().ToString("N"), "blocked");
        Directory.CreateDirectory(Path.GetDirectoryName(blockedPath));
        File.WriteAllText(blockedPath, "not-a-directory");
        SetField(recorder, "outputDirectoryName", blockedPath);
        SetField(bootstrap, "width", 4);
        SetField(bootstrap, "depth", 4);
        SetField(bootstrap, "droneCount", 1);
        SetField(bootstrap, "resetQueued", true);
        Action<bool, string> resetHandler = (Action<bool, string>)Delegate.CreateDelegate(
            typeof(Action<bool, string>),
            scripts,
            scripts.GetType().GetMethod("OnResetCompleted", BindingFlags.Instance | BindingFlags.NonPublic));
        EventInfo resetCompleted = bootstrap.GetType().GetEvent("ResetCompleted", BindingFlags.Instance | BindingFlags.Public);
        resetCompleted.AddEventHandler(bootstrap, resetHandler);

        bool previousIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            IEnumerator failed = (IEnumerator)InvokePrivate(bootstrap, "ResetDemoNextFrame");
            while (failed.MoveNext()) { }

            Assert.That(GetProperty<bool>(bootstrap, "LastResetSucceeded"), Is.False);
            Assert.That(GetProperty<string>(bootstrap, "LastResetError"), Does.Contain("Telemetry session could not be started"));
            Assert.That(forestObject.transform.childCount, Is.Zero);
            Assert.That(((IList)GetField(forest, "spawnedTreePositions")).Count, Is.Zero);
            Assert.That((bool)InvokePublic(forest, "TrySpawnTrees"), Is.True);
            Assert.That(forestObject.transform.childCount, Is.EqualTo(4),
                "retry must generate the configured total, not append to failed startup state");
            Assert.That(GetProperty<bool>(bootstrap, "IsGameplayStopped"), Is.False);
        }
        finally
        {
            LogAssert.ignoreFailingMessages = previousIgnore;
            resetCompleted.RemoveEventHandler(bootstrap, resetHandler);
            UnityEngine.Object.DestroyImmediate(scriptsObject);
            UnityEngine.Object.DestroyImmediate(explorerObject);
            UnityEngine.Object.DestroyImmediate(forestObject);
            UnityEngine.Object.DestroyImmediate(treePrefab);
            UnityEngine.Object.DestroyImmediate(terrainObject);
            UnityEngine.Object.DestroyImmediate(terrainData);
            DestroySpawnedBootstrapObjects();
            if (File.Exists(blockedPath)) File.Delete(blockedPath);
            string parent = Path.GetDirectoryName(blockedPath);
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [Test]
    public void ResetFailsWhenRequiredTelemetrySessionCannotStart()
    {
        SetField(bootstrap, "width", 4);
        SetField(bootstrap, "depth", 4);
        SetField(bootstrap, "droneCount", 1);
        string blockedPath = Path.Combine(Application.persistentDataPath, "DroneTelemetryTests", Guid.NewGuid().ToString("N"), "blocked");
        Directory.CreateDirectory(Path.GetDirectoryName(blockedPath));
        File.WriteAllText(blockedPath, "not-a-directory");
        SetField(recorder, "outputDirectoryName", blockedPath);
        SetField(bootstrap, "resetQueued", true);

        bool completionCalled = false;
        bool completionSucceeded = true;
        Action<bool, string> handler = (succeeded, _) =>
        {
            completionCalled = true;
            completionSucceeded = succeeded;
        };
        EventInfo resetCompleted = bootstrap.GetType().GetEvent("ResetCompleted", BindingFlags.Instance | BindingFlags.Public);
        resetCompleted.AddEventHandler(bootstrap, handler);

        bool previousIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            IEnumerator reset = (IEnumerator)InvokePrivate(bootstrap, "ResetDemoNextFrame");
            while (reset.MoveNext())
            {
            }

            Assert.That(completionCalled, Is.True);
            Assert.That(completionSucceeded, Is.False);
            Assert.That(GetProperty<bool>(bootstrap, "LastResetSucceeded"), Is.False);
            Assert.That(GetProperty<string>(bootstrap, "LastResetError"), Does.Contain("Telemetry session could not be started"));
            Assert.That(GetProperty<bool>(bootstrap, "IsTelemetrySessionActive"), Is.False);
            Assert.That(GetProperty<bool>(bootstrap, "MissionComplete"), Is.True,
                "failed startup must freeze the newly built simulation");
        }
        finally
        {
            LogAssert.ignoreFailingMessages = previousIgnore;
            resetCompleted.RemoveEventHandler(bootstrap, handler);
            DestroySpawnedBootstrapObjects();
            if (File.Exists(blockedPath))
            {
                File.Delete(blockedPath);
            }
            string parent = Path.GetDirectoryName(blockedPath);
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, true);
            }
        }
    }

    [Test]
    public void FailedResetKeepsOldWorldAndSessionAndRetryCanCommitReset()
    {
        SetField(bootstrap, "width", 4);
        SetField(bootstrap, "depth", 4);
        SetField(bootstrap, "droneCount", 1);
        Assert.That((bool)InvokePublic(recorder, "TryBeginSession", Activator.CreateInstance(configType)), Is.True);
        string oldSessionId = GetProperty<string>(recorder, "ActiveSessionId");
        var oldWorldObject = new GameObject("reset-transaction-old-world-test");
        InvokePrivate(bootstrap, "RegisterSpawned", oldWorldObject);

        string summaryPath = GetProperty<string>(recorder, "SummaryFilePath");
        File.Delete(summaryPath);
        Directory.CreateDirectory(summaryPath);
        SetField(bootstrap, "resetQueued", true);

        bool previousIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            IEnumerator failedReset = (IEnumerator)InvokePrivate(bootstrap, "ResetDemoNextFrame");
            while (failedReset.MoveNext())
            {
            }
        }
        finally
        {
            LogAssert.ignoreFailingMessages = previousIgnore;
        }

        Assert.That(GetProperty<bool>(bootstrap, "LastResetSucceeded"), Is.False);
        Assert.That(GetProperty<bool>(bootstrap, "IsResetQueued"), Is.False);
        Assert.That(oldWorldObject.activeSelf, Is.True, "reset mutated the old world before telemetry committed");
        Assert.That(GetProperty<bool>(recorder, "HasActiveSession"), Is.True);
        Assert.That(GetProperty<string>(recorder, "ActiveSessionId"), Is.EqualTo(oldSessionId));

        Directory.Delete(summaryPath, true);
        SetField(bootstrap, "resetQueued", true);
        previousIgnore = LogAssert.ignoreFailingMessages;
        LogAssert.ignoreFailingMessages = true;
        try
        {
            IEnumerator recoveredReset = (IEnumerator)InvokePrivate(bootstrap, "ResetDemoNextFrame");
            while (recoveredReset.MoveNext())
            {
            }

            Assert.That(GetProperty<bool>(bootstrap, "LastResetSucceeded"), Is.True);
            Assert.That(GetProperty<bool>(bootstrap, "IsResetQueued"), Is.False);
            Assert.That(oldWorldObject.activeSelf, Is.False);
            Assert.That(GetProperty<bool>(recorder, "HasActiveSession"), Is.True);
            Assert.That(GetProperty<string>(recorder, "ActiveSessionId"), Is.Not.EqualTo(oldSessionId));

            foreach (GameObject spawned in (IEnumerable)GetField(bootstrap, "spawnedObjects"))
            {
                if (spawned != null)
                {
                    UnityEngine.Object.DestroyImmediate(spawned);
                }
            }
        }
        finally
        {
            LogAssert.ignoreFailingMessages = previousIgnore;
        }
    }

    [Test]
    public void FreezingOneBootstrapDisablesOnlyItsConfiguredExplorerAndDrones()
    {
        Type bootstrapType = RequireType("DroneSwarmDemoBootstrap, Assembly-CSharp");
        Type scriptsType = RequireType("ScriptsControl, Assembly-CSharp");
        Type explorerType = RequireType("Explorer, Assembly-CSharp");
        Type droneType = RequireType("DroneFrontierExplorer, Assembly-CSharp");

        var otherBootstrapObject = new GameObject("isolation-bootstrap-b");
        Component otherBootstrap = otherBootstrapObject.AddComponent(bootstrapType);
        var explorerAObject = new GameObject("isolation-explorer-a");
        explorerAObject.AddComponent<CharacterController>();
        Component explorerA = explorerAObject.AddComponent(explorerType);
        var explorerBObject = new GameObject("isolation-explorer-b");
        explorerBObject.AddComponent<CharacterController>();
        Component explorerB = explorerBObject.AddComponent(explorerType);
        var scriptsAObject = new GameObject("isolation-scripts-a");
        Component scriptsA = scriptsAObject.AddComponent(scriptsType);
        var scriptsBObject = new GameObject("isolation-scripts-b");
        Component scriptsB = scriptsBObject.AddComponent(scriptsType);
        var droneAObject = new GameObject("isolation-drone-a");
        Component droneA = droneAObject.AddComponent(droneType);
        var droneBObject = new GameObject("isolation-drone-b");
        Component droneB = droneBObject.AddComponent(droneType);

        SetField(scriptsA, "droneSwarmDemoBootstrap", bootstrap);
        SetField(scriptsA, "explorer", explorerA);
        SetField(scriptsB, "droneSwarmDemoBootstrap", otherBootstrap);
        SetField(scriptsB, "explorer", explorerB);
        ((IList)GetField(bootstrap, "explorers")).Add(droneA);
        ((IList)GetField(otherBootstrap, "explorers")).Add(droneB);

        try
        {
            // humanExplorer is intentionally unwired to exercise compatibility
            // resolution through each scene's explicit ScriptsControl relationship.
            InvokePrivate(bootstrap, "FreezeSimulationComponents");

            Assert.That(((Behaviour)explorerA).enabled, Is.False);
            Assert.That(((Behaviour)droneA).enabled, Is.False);
            Assert.That(((Behaviour)explorerB).enabled, Is.True,
                "stopping bootstrap A must not freeze bootstrap B's human Explorer");
            Assert.That(((Behaviour)droneB).enabled, Is.True,
                "bootstrap-owned drone isolation must remain intact");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(scriptsAObject);
            UnityEngine.Object.DestroyImmediate(scriptsBObject);
            UnityEngine.Object.DestroyImmediate(explorerAObject);
            UnityEngine.Object.DestroyImmediate(explorerBObject);
            UnityEngine.Object.DestroyImmediate(droneAObject);
            UnityEngine.Object.DestroyImmediate(droneBObject);
            UnityEngine.Object.DestroyImmediate(otherBootstrapObject);
        }
    }

    private static void PreparePendingSetup(
        Component scripts,
        string setupKind,
        bool replayActivated,
        bool initialRollbackRequired)
    {
        FieldInfo kindField = scripts.GetType().GetField("pendingSetupKind", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(kindField, Is.Not.Null);
        kindField.SetValue(scripts, Enum.Parse(kindField.FieldType, setupKind));
        SetField(scripts, "setupInProgress", true);
        SetField(scripts, "waitingForReset", true);
        SetField(scripts, "pendingReplayActivated", replayActivated);
        SetField(scripts, "pendingInitialRollbackRequired", initialRollbackRequired);
    }

    private void DestroySpawnedBootstrapObjects()
    {
        foreach (GameObject spawned in (IEnumerable)GetField(bootstrap, "spawnedObjects"))
        {
            if (spawned != null)
            {
                UnityEngine.Object.DestroyImmediate(spawned);
            }
        }
    }

    private static object InvokePublic(Component target, string name, params object[] arguments)
    {
        MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.That(method, Is.Not.Null, $"Missing public method {name}");
        return method.Invoke(target, arguments);
    }

    private static object InvokePrivate(Component target, string name, params object[] arguments)
    {
        MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Missing private method {name}");
        return method.Invoke(target, arguments);
    }

    private static T GetProperty<T>(Component target, string name)
    {
        PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.That(property, Is.Not.Null, $"Missing property {name}");
        return (T)property.GetValue(target);
    }

    private static object GetField(Component target, string name)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field {name}");
        return field.GetValue(target);
    }

    private static void SetField(Component target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field {name}");
        field.SetValue(target, value);
    }

    private static Type RequireType(string assemblyQualifiedName)
    {
        Type type = Type.GetType(assemblyQualifiedName);
        Assert.That(type, Is.Not.Null, $"Missing type {assemblyQualifiedName}");
        return type;
    }
}
