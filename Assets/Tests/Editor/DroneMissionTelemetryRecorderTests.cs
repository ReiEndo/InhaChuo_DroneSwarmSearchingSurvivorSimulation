using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class DroneMissionTelemetryRecorderTests
{
    private GameObject gameObject;
    private Component recorder;
    private Type recorderType;
    private Type configType;
    private Type cellType;
    private string outputDirectory;

    [SetUp]
    public void SetUp()
    {
        string recoveryDirectory = Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery");
        if (Directory.Exists(recoveryDirectory)) Directory.Delete(recoveryDirectory, true);
        recorderType = RequireType("DroneMissionTelemetryRecorder, Assembly-CSharp");
        SetMachineIdentityOverride("telemetry-test-machine");
        configType = RequireType("DroneMissionSessionConfig, Assembly-CSharp");
        cellType = RequireType("DroneNative+DroneVec3i, Assembly-CSharp");
        gameObject = new GameObject("telemetry-recorder-test");
        recorder = gameObject.AddComponent(recorderType);
        SetField("outputDirectoryName", Path.Combine("DroneTelemetryTests", Guid.NewGuid().ToString("N")));
        SetField("logOutputPathOnSessionStart", false);
        outputDirectory = GetProperty<string>("OutputDirectory");
    }

    [TearDown]
    public void TearDown()
    {
        SetProcessProbeOverride(null);
        SetMachineIdentityOverride(null);
        UnityEngine.Object.DestroyImmediate(gameObject);
        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, true);
        }
        string recoveryDirectory = Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery");
        if (Directory.Exists(recoveryDirectory)) Directory.Delete(recoveryDirectory, true);
    }

    [Test]
    public void OrdinaryEventsAppendValidRows()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        Invoke("RecordDroneInformed", 1, Activator.CreateInstance(cellType), 1);
        Invoke("RecordDroneInformed", 2, Activator.CreateInstance(cellType), 2);
        Assert.That(End(), Is.True);

        string[] lines = File.ReadAllLines(EventPath);
        Assert.That(lines, Has.Length.EqualTo(5));
        Assert.That(lines.Count(line => line.Contains(",drone_informed,")), Is.EqualTo(2));
    }

    [Test]
    public void FailedOrdinaryEventIsRetriedBeforeLaterEventsAndClearsFailureAfterFlush()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string originalEventFile = File.ReadAllText(EventPath);
        File.Delete(EventPath);
        Directory.CreateDirectory(EventPath);

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        Invoke("RecordDroneInformed", 1, Activator.CreateInstance(cellType), 1);
        Assert.That(GetProperty<bool>("HasPersistenceFailure"), Is.True);

        Directory.Delete(EventPath);
        File.WriteAllText(EventPath, originalEventFile);
        Invoke("RecordDroneInformed", 2, Activator.CreateInstance(cellType), 2);

        Assert.That(GetProperty<bool>("HasPersistenceFailure"), Is.False);
        Assert.That(End(), Is.True);
        string[] eventLines = File.ReadAllLines(EventPath);
        int first = Array.FindIndex(eventLines, line => line.Contains(",drone_informed,1,,1,"));
        int second = Array.FindIndex(eventLines, line => line.Contains(",drone_informed,2,,2,"));
        Assert.That(first, Is.GreaterThan(0));
        Assert.That(second, Is.GreaterThan(first));
        Assert.That(eventLines.Count(line => line.Contains(",drone_informed,")), Is.EqualTo(2));
    }

    [Test]
    public void AmbiguousOrdinaryEventRetryDoesNotDuplicateCompletedRow()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string originalEventFile = File.ReadAllText(EventPath);
        File.Delete(EventPath);
        Directory.CreateDirectory(EventPath);

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        Invoke("RecordDroneInformed", 1, Activator.CreateInstance(cellType), 1);

        object queue = GetField("pendingOrdinaryEvents");
        object pending = queue.GetType().GetMethod("Peek").Invoke(queue, null);
        string[] pendingRow = (string[])pending.GetType().GetField("Row").GetValue(pending);
        MethodInfo toCsvLine = recorderType.GetMethod("ToCsvLine", BindingFlags.Static | BindingFlags.NonPublic);
        string persistedRow = (string)toCsvLine.Invoke(null, new object[] { pendingRow });

        string[] originalLines = originalEventFile.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        string externalRow = originalLines.Length > 1
            ? originalLines[1].Replace(",session_start,", ",external_writer,")
            : throw new AssertionException("missing session_start row");
        Directory.Delete(EventPath);
        File.WriteAllText(
            EventPath,
            originalEventFile + persistedRow + Environment.NewLine + externalRow + Environment.NewLine);
        Invoke("RecordDroneInformed", 2, Activator.CreateInstance(cellType), 2);
        Assert.That(End(), Is.True);

        string[] eventLines = File.ReadAllLines(EventPath);
        Assert.That(eventLines.Count(line => line.Contains(",drone_informed,1,,1,")), Is.EqualTo(1));
        Assert.That(eventLines.Count(line => line.Contains(",drone_informed,2,,2,")), Is.EqualTo(1));
        Assert.That(eventLines.Count(line => line.Contains(",external_writer,")), Is.EqualTo(1));
    }

    [Test]
    public void SessionEndFlushesFailedOrdinaryEventBeforeFinalRows()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string originalEventFile = File.ReadAllText(EventPath);
        File.Delete(EventPath);
        Directory.CreateDirectory(EventPath);

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        Invoke("RecordDroneInformed", 1, Activator.CreateInstance(cellType), 1);
        Directory.Delete(EventPath);
        File.WriteAllText(EventPath, originalEventFile);

        Assert.That(End(), Is.True);
        Assert.That(GetProperty<bool>("HasPersistenceFailure"), Is.False);
        string[] eventLines = File.ReadAllLines(EventPath);
        int ordinary = Array.FindIndex(eventLines, line => line.Contains(",drone_informed,"));
        int sessionEnd = Array.FindIndex(eventLines, line => line.Contains(",session_end,"));
        Assert.That(ordinary, Is.GreaterThan(0));
        Assert.That(sessionEnd, Is.GreaterThan(ordinary));
    }

    [Test]
    public void ExternalCorruptionIsRejectedAtSessionBoundary()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        File.AppendAllText(EventPath, "malformed,row\n");

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Malformed record"));
        Assert.That(End(), Is.False);
        Assert.That(GetProperty<bool>("HasPersistenceFailure"), Is.True);
        Assert.That(File.ReadAllText(EventPath), Does.Not.Contain(",session_end,"));
    }

    [Test]
    public void RetryableFinalRowIsNotDuplicated()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string summaryPath = GetProperty<string>("SummaryFilePath");
        string summaryHeader = File.ReadAllText(summaryPath);
        File.Delete(summaryPath);
        Directory.CreateDirectory(summaryPath);

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        Assert.That(End(), Is.False);
        // Simulate losing the success acknowledgement after the final event was
        // flushed. The retry must discover the complete row already on disk.
        SetField("endEventWritten", false);
        Directory.Delete(summaryPath);
        File.WriteAllText(summaryPath, summaryHeader);

        Assert.That((bool)Invoke("TryEndSession", "ignored-on-retry", false), Is.True);
        Assert.That(GetProperty<bool>("HasPersistenceFailure"), Is.False);
        Assert.That(GetProperty<string>("LastPersistenceError"), Is.Empty);
        Assert.That(File.ReadAllLines(EventPath).Count(line => line.Contains(",session_end,")), Is.EqualTo(1));
    }

    [TestCase("event", "delete")]
    [TestCase("event", "truncate")]
    [TestCase("event", "replace")]
    [TestCase("summary", "delete")]
    [TestCase("summary", "truncate")]
    [TestCase("summary", "replace")]
    public void FrozenFinalRowIsReappendedWhenItsOutputDisappearsBeforeRetry(
        string output,
        string mutation)
    {
        Assert.That(Begin(NewConfig()), Is.True);

        string summaryPath = GetProperty<string>("SummaryFilePath");
        string testedPath = output == "event" ? EventPath : summaryPath;
        string blockerPath = output == "event" ? summaryPath : EventPath;
        string blockerContents = File.ReadAllText(blockerPath);
        string testedHeader = File.ReadLines(testedPath).First() + Environment.NewLine;

        File.Delete(blockerPath);
        Directory.CreateDirectory(blockerPath);

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        Assert.That(End(), Is.False);
        Assert.That(GetProperty<bool>("HasPendingSessionEnd"), Is.True);
        Assert.That(
            File.ReadAllLines(testedPath).Count(line =>
                output == "event" ? line.Contains(",session_end,") : line.Contains(",complete,true,")),
            Is.EqualTo(1), "the first final append must have succeeded before storage is mutated");

        MutateOutput(testedPath, testedHeader, mutation);
        Directory.Delete(blockerPath);
        File.WriteAllText(blockerPath, blockerContents);

        Assert.That((bool)Invoke("TryEndSession", "ignored-on-retry", false), Is.True);
        Assert.That(GetProperty<bool>("HasActiveSession"), Is.False);
        Assert.That(GetProperty<bool>("HasPersistenceFailure"), Is.False);
        Assert.That(
            File.ReadAllLines(testedPath).Count(line =>
                output == "event" ? line.Contains(",session_end,") : line.Contains(",complete,true,")),
            Is.EqualTo(1), "the frozen row must be restored exactly once");
    }

    [Test]
    public void MultilineCsvFieldSurvivesAppendAndBoundaryValidation()
    {
        object config = NewConfig();
        configType.GetField("SceneName").SetValue(config, "first line\nsecond line, \"quoted\"");

        Assert.That(Begin(config), Is.True);
        Assert.That(End(), Is.True);
        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        Assert.That(File.ReadAllText(EventPath), Does.Contain("\"scene=first line\nsecond line, \"\"quoted\"\""));
    }

    [Test]
    public void OwnedSessionsAndCountsDoNotAddFullFileValidationPasses()
    {
        const int sessionCount = 200;
        for (int i = 0; i < sessionCount; i++)
        {
            Assert.That(Begin(NewConfig()), Is.True, $"session {i}");
            Assert.That(End(), Is.True, $"session {i}");
            Assert.That((int)Invoke("CountSessionsWithEndReason", "complete"), Is.EqualTo(i + 1));
        }

        Assert.That(GetField("fullCsvValidationCount"), Is.EqualTo(0),
            "owned appends and unchanged storage boundaries must remain O(1)");
        Assert.That(File.ReadAllLines(GetProperty<string>("SummaryFilePath")), Has.Length.EqualTo(sessionCount + 1));
    }

    [Test]
    public void ExternalSummaryAppendInvalidatesValidationAndRebuildsCountsOnce()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        Assert.That(End(), Is.True);
        string summaryPath = GetProperty<string>("SummaryFilePath");
        string persistedRow = File.ReadAllLines(summaryPath)[1];
        int validationsBefore = (int)GetField("fullCsvValidationCount");

        File.AppendAllText(summaryPath, persistedRow + Environment.NewLine);

        Assert.That((int)Invoke("CountSessionsWithEndReason", "complete"), Is.EqualTo(2));
        Assert.That(GetField("fullCsvValidationCount"), Is.EqualTo(validationsBefore + 1));
        for (int i = 0; i < 100; i++)
        {
            Assert.That((int)Invoke("CountSessionsWithEndReason", "complete"), Is.EqualTo(2));
        }
        Assert.That(GetField("fullCsvValidationCount"), Is.EqualTo(validationsBefore + 1),
            "unchanged recounts must use the validated end_reason index");
    }

    [Test]
    public void ConcurrentRecordersSerializeAppendsAndRebuildExactCount()
    {
        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        var secondObject = new GameObject("telemetry-recorder-concurrent-test");
        Component secondRecorder = secondObject.AddComponent(recorderType);
        try
        {
            FieldInfo outputField = recorderType.GetField(
                "outputDirectoryName", BindingFlags.Instance | BindingFlags.NonPublic);
            outputField.SetValue(secondRecorder, GetField("outputDirectoryName"));
            MethodInfo append = recorderType.GetMethod(
                "AppendCsvRow", BindingFlags.Instance | BindingFlags.NonPublic);
            string[] header = (string[])recorderType.GetField(
                "SummaryHeader", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Assert.That(append, Is.Not.Null);
            string summaryPath = GetProperty<string>("SummaryFilePath");
            const int rowCount = 24;
            var tasks = new Task[rowCount];
            for (int i = 0; i < rowCount; i++)
            {
                int rowIndex = i;
                Component owner = (i & 1) == 0 ? recorder : secondRecorder;
                tasks[i] = Task.Run(() =>
                {
                    var row = new string[header.Length];
                    for (int field = 0; field < row.Length; field++)
                    {
                        row[field] = string.Empty;
                    }
                    row[0] = $"concurrent-{rowIndex}";
                    row[7] = $"2026-01-01T00:00:{rowIndex:00}.0000000Z";
                    row[8] = "complete";
                    row[9] = "true";
                    bool appended = (bool)append.Invoke(
                        owner, new object[] { summaryPath, header, row, true, false, null });
                    Assert.That(appended, Is.True);
                });
            }

            Assert.That(Task.WaitAll(tasks, TimeSpan.FromSeconds(10)), Is.True);
            Assert.That((int)Invoke("CountSessionsWithEndReason", "complete"), Is.EqualTo(rowCount));
            Assert.That(File.ReadAllLines(GetProperty<string>("SummaryFilePath")), Has.Length.EqualTo(rowCount + 1));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(secondObject);
        }
    }

    [Test]
    public void BatchScopedCountUsesMatchingReasonAndUniqueSessionIdsOnly()
    {
        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        AppendSummaryRow("a-1", "batch-a", "mission_complete");
        AppendSummaryRow("b-1", "batch-b", "mission_complete");
        AppendSummaryRow("a-1", "batch-a", "mission_complete");
        AppendSummaryRow("a-failed", "batch-a", "timeout");
        AppendSummaryRow("a-2", "batch-a", "mission_complete");

        Assert.That(
            (int)Invoke("CountUniqueSessionsWithEndReasonForBatch", "mission_complete", "batch-a"),
            Is.EqualTo(2));
        Assert.That(
            (int)Invoke("CountUniqueSessionsWithEndReasonForBatch", "mission_complete", "batch-b"),
            Is.EqualTo(1));
        Assert.That((int)Invoke("CountSessionsWithEndReason", "mission_complete"), Is.EqualTo(4),
            "the compatibility API must continue to count physical rows globally");
    }

    [Test]
    public void ExternalBatchRowsRebuildUniqueCacheAndMalformedRowsFailClosed()
    {
        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        AppendSummaryRow("owned", "target-batch", "mission_complete");
        Assert.That(
            (int)Invoke("CountUniqueSessionsWithEndReasonForBatch", "mission_complete", "target-batch"),
            Is.EqualTo(1));
        int validationsBefore = (int)GetField("fullCsvValidationCount");

        string summaryPath = GetProperty<string>("SummaryFilePath");
        File.AppendAllText(
            summaryPath,
            ToSummaryCsvLine("external", "other-batch", "mission_complete") + Environment.NewLine +
            ToSummaryCsvLine("external-target", "target-batch", "mission_complete") + Environment.NewLine +
            ToSummaryCsvLine("owned", "target-batch", "mission_complete") + Environment.NewLine);

        Assert.That(
            (int)Invoke("CountUniqueSessionsWithEndReasonForBatch", "mission_complete", "target-batch"),
            Is.EqualTo(2));
        Assert.That(GetField("fullCsvValidationCount"), Is.EqualTo(validationsBefore + 1));

        File.AppendAllText(summaryPath, "malformed,row" + Environment.NewLine);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Malformed record"));
        object[] arguments = { "mission_complete", "target-batch", 123 };
        MethodInfo tryCount = recorderType.GetMethod(
            "TryCountUniqueSessionsWithEndReasonForBatch", BindingFlags.Instance | BindingFlags.Public);
        Assert.That((bool)tryCount.Invoke(recorder, arguments), Is.False);
        Assert.That(arguments[2], Is.EqualTo(0));
    }

    [Test]
    public void ConcurrentInterleavedAppendsReachExactUniqueTargetWithoutDuplicates()
    {
        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        const int target = 12;
        MethodInfo append = recorderType.GetMethod(
            "AppendCsvRow", BindingFlags.Instance | BindingFlags.NonPublic);
        string[] header = (string[])recorderType.GetField(
            "SummaryHeader", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        string summaryPath = GetProperty<string>("SummaryFilePath");
        var tasks = new Task<bool>[target * 3];
        for (int i = 0; i < tasks.Length; i++)
        {
            int rowIndex = i;
            tasks[i] = Task.Run(() =>
            {
                var row = new string[header.Length];
                for (int field = 0; field < row.Length; field++)
                {
                    row[field] = string.Empty;
                }
                row[0] = rowIndex < target * 2 ? $"target-{rowIndex % target}" : $"external-{rowIndex}";
                row[1] = rowIndex < target * 2 ? "target-batch" : "external-batch";
                row[7] = "2026-01-01T00:00:00.0000000Z";
                row[8] = "mission_complete";
                row[9] = "true";
                return (bool)append.Invoke(
                    recorder,
                    new object[] { summaryPath, header, row, false, false, null });
            });
        }

        Assert.That(Task.WaitAll(tasks, TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(tasks.All(task => task.Result), Is.True);
        Assert.That(
            (int)Invoke("CountUniqueSessionsWithEndReasonForBatch", "mission_complete", "target-batch"),
            Is.EqualTo(target));
        Assert.That((int)Invoke("CountSessionsWithEndReason", "mission_complete"),
            Is.EqualTo(target * 3));
    }

    [Test]
    public void ContendedProcessSimulationPublishesCorrectCountCache()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        Assert.That(End(), Is.True);
        string summaryPath = GetProperty<string>("SummaryFilePath");
        string ownedRow = File.ReadAllLines(summaryPath)[1];
        string externalRow = "external-process" + ownedRow.Substring(ownedRow.IndexOf(','));
        int validationsBefore = (int)GetField("fullCsvValidationCount");
        using (var writerReady = new ManualResetEventSlim(false))
        {
            var writer = new Thread(() =>
            {
                using (new FileStream(summaryPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    File.AppendAllText(summaryPath, externalRow + Environment.NewLine);
                    writerReady.Set();
                    Thread.Sleep(150);
                }
            });
            writer.Start();
            Assert.That(writerReady.Wait(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That((int)Invoke("CountSessionsWithEndReason", "complete"), Is.EqualTo(2));
            writer.Join();
        }

        Assert.That(GetField("fullCsvValidationCount"), Is.EqualTo(validationsBefore + 1));
        Assert.That((int)Invoke("CountSessionsWithEndReason", "complete"), Is.EqualTo(2));
        Assert.That(GetField("fullCsvValidationCount"), Is.EqualTo(validationsBefore + 1));
    }

    private static void MutateOutput(string path, string header, string mutation)
    {
        switch (mutation)
        {
            case "delete":
                File.Delete(path);
                break;
            case "truncate":
                using (File.Create(path))
                {
                }
                break;
            case "replace":
                string replacement = path + ".replacement";
                File.WriteAllText(replacement, header);
                File.Delete(path);
                File.Move(replacement, path);
                break;
            default:
                Assert.Fail($"Unknown mutation {mutation}");
                break;
        }
    }

    [Test]
    public void HardCrashAfterStartIsClassifiedAsProcessInterruptedOnRecreation()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string crashedSessionId = GetProperty<string>("ActiveSessionId");
        SimulateHardCrash(gameObject, recorder);
        RecreateRecorderAfterCrash();

        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        Assert.That(File.ReadAllLines(EventPath).Count(line =>
            line.StartsWith(crashedSessionId + ",") && line.Contains(",session_start,")), Is.EqualTo(1));
        Assert.That(File.ReadAllLines(EventPath).Count(line =>
            line.StartsWith(crashedSessionId + ",") && line.Contains(",session_end,") &&
            line.EndsWith(",process_interrupted")), Is.EqualTo(1));
        string summary = File.ReadAllLines(SummaryPath).Single(line => line.StartsWith(crashedSessionId + ","));
        Assert.That(summary, Does.Contain(",process_interrupted,false,"));
        Assert.That((int)Invoke("CountSessionsWithEndReason", "mission_complete"), Is.Zero,
            "an interrupted session must never satisfy the timed batch's valid end-reason target");
        Assert.That(Directory.GetFiles(
            Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery"),
            "pending_rows.*.journal"), Is.Empty);
    }

    [Test]
    public void HardCrashAfterTelemetryUpdateRecoversLastAggregateCheckpoint()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string crashedSessionId = GetProperty<string>("ActiveSessionId");
        Invoke("RecordDroneInformed", 71, Activator.CreateInstance(cellType), 1);
        SimulateHardCrash(gameObject, recorder);
        RecreateRecorderAfterCrash();

        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        Assert.That(File.ReadAllLines(EventPath).Count(line => line.Contains(",drone_informed,71,,1,")), Is.EqualTo(1));
        string summary = File.ReadAllLines(SummaryPath).Single(line => line.StartsWith(crashedSessionId + ","));
        Assert.That(summary, Does.EndWith(",0,1"),
            "meaningful transitions checkpoint aggregates; only progress after the last bounded checkpoint may be lost");
    }

    [Test]
    public void ActiveCheckpointIsNotRewrittenEveryFrame()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string journal = Directory.GetFiles(
            Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery"),
            "pending_rows.*.journal").Single();
        byte[] initial = File.ReadAllBytes(journal);

        InvokeNonPublic("Update");
        InvokeNonPublic("Update");
        CollectionAssert.AreEqual(initial, File.ReadAllBytes(journal),
            "normal frames must not perform durable checkpoint I/O");

        SetField("nextActiveCheckpointRealtime", 0f);
        InvokeNonPublic("Update");
        CollectionAssert.AreNotEqual(initial, File.ReadAllBytes(journal),
            "the bounded interval refreshes elapsed progress; a crash can lose up to this interval");
    }

    [Test]
    public void AmbiguousInterruptedReplayIsIdempotentAndPreservesExistingEvents()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        Invoke("RecordDroneInformed", 72, Activator.CreateInstance(cellType), 1);
        string summaryHeader = File.ReadAllText(SummaryPath);
        SimulateHardCrash(gameObject, recorder);
        File.Delete(SummaryPath);
        Directory.CreateDirectory(SummaryPath);
        RecreateRecorderAfterCrash();

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.False);
        Assert.That(File.ReadAllLines(EventPath).Count(line => line.Contains(",session_end,") &&
            line.EndsWith(",process_interrupted")), Is.EqualTo(1));

        Directory.Delete(SummaryPath);
        File.WriteAllText(SummaryPath, summaryHeader);
        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        Assert.That(File.ReadAllLines(EventPath).Count(line => line.Contains(",drone_informed,72,,1,")), Is.EqualTo(1));
        Assert.That(File.ReadAllLines(EventPath).Count(line => line.Contains(",session_end,") &&
            line.EndsWith(",process_interrupted")), Is.EqualTo(1));
    }

    [Test]
    public void ConcurrentActiveSessionCheckpointsRecoverIndependentlyAfterCrash()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        var secondObject = new GameObject("telemetry-recorder-active-checkpoint-concurrent-test");
        Component second = secondObject.AddComponent(recorderType);
        SetFieldOn(second, "outputDirectoryName", Path.GetRelativePath(Application.persistentDataPath, outputDirectory));
        SetFieldOn(second, "logOutputPathOnSessionStart", false);
        Assert.That((bool)InvokeOn(second, "TryBeginSession", NewConfig()), Is.True,
            "a live same-process checkpoint must not be mistaken for a crash");

        SimulateHardCrash(gameObject, recorder);
        SimulateHardCrash(secondObject, second);
        RecreateRecorderAfterCrash();
        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        Assert.That(File.ReadAllLines(SummaryPath).Count(line => line.Contains(",process_interrupted,false,")),
            Is.EqualTo(2));
        Assert.That(File.ReadAllLines(EventPath).Count(line => line.Contains(",session_end,") &&
            line.EndsWith(",process_interrupted")), Is.EqualTo(2));
    }

    [Test]
    public void SecondLiveProcessDoesNotFinalizeOrDeleteFirstProcessCheckpoint()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string firstSessionId = GetProperty<string>("ActiveSessionId");
        const long simulatedStartUtcTicks = 638900000000000000L;
        RewriteActiveJournalOwner(journal =>
        {
            SetNestedField(journal, "ProcessNonce", Guid.NewGuid().ToString("N"));
            SetNestedField(journal, "ProcessId", 4242);
            SetNestedField(journal, "ProcessStartUtcTicks", simulatedStartUtcTicks);
            SetNestedField(journal, "HeartbeatUtcTicks", DateTime.UtcNow.Ticks);
        });
        SetProcessProbeOverride(_ => simulatedStartUtcTicks);

        var secondObject = new GameObject("telemetry-recorder-second-process-simulation-test");
        Component second = secondObject.AddComponent(recorderType);
        try
        {
            SetFieldOn(second, "outputDirectoryName", Path.GetRelativePath(Application.persistentDataPath, outputDirectory));
            SetFieldOn(second, "logOutputPathOnSessionStart", false);
            Assert.That((bool)InvokeOn(second, "TryBeginSession", NewConfig()), Is.True);
            Assert.That(File.ReadAllLines(EventPath).Count(line =>
                line.StartsWith(firstSessionId + ",") && line.Contains(",session_end,")), Is.Zero);
            Assert.That(Directory.GetFiles(
                Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery"),
                "pending_rows.*.journal"), Has.Length.EqualTo(2));
            Assert.That((bool)InvokeOn(second, "TryEndSession", "complete", true), Is.True);
            Assert.That(End(), Is.True);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(secondObject);
        }
    }

    [Test]
    public void ReusedPidWithDifferentStartTimeRecoversDeadOwner()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string crashedSessionId = GetProperty<string>("ActiveSessionId");
        const long reusedProcessStartUtcTicks = 638900000000000000L;
        RewriteActiveJournalOwner(journal =>
        {
            SetNestedField(journal, "ProcessNonce", Guid.NewGuid().ToString("N"));
            SetNestedField(journal, "ProcessId", 4242);
            SetNestedField(journal, "ProcessStartUtcTicks", reusedProcessStartUtcTicks + TimeSpan.TicksPerSecond);
        });
        SetProcessProbeOverride(_ => reusedProcessStartUtcTicks);
        SimulateHardCrash(gameObject, recorder);
        RecreateRecorderAfterCrash();

        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        Assert.That(File.ReadAllLines(EventPath).Count(line =>
            line.StartsWith(crashedSessionId + ",") && line.EndsWith(",process_interrupted")), Is.EqualTo(1));
    }

    [Test]
    public void RemoteActiveOwnerIsExposedAndPreservedWithoutFinalization()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string crashedSessionId = GetProperty<string>("ActiveSessionId");
        RewriteActiveJournalOwner(journal =>
        {
            SetNestedField(journal, "ProcessNonce", Guid.NewGuid().ToString("N"));
            SetNestedField(journal, "MachineIdentity", "remote-machine-identity");
        });
        SimulateHardCrash(gameObject, recorder);
        RecreateRecorderAfterCrash();

        LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Deferring ambiguous active checkpoint owner"));
        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.False);
        Assert.That(File.ReadAllLines(EventPath).Any(line =>
            line.StartsWith(crashedSessionId + ",") && line.Contains(",session_end,")), Is.False);
        Assert.That(Directory.GetFiles(
            Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery"),
            "pending_rows.*.journal"), Has.Length.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AmbiguousLocalOwnerIsPreservedWhenProcessApiIsUnavailableOrHeartbeatIsStale(bool staleHeartbeat)
    {
        Assert.That(Begin(NewConfig()), Is.True);
        RewriteActiveJournalOwner(journal =>
        {
            SetNestedField(journal, "ProcessNonce", Guid.NewGuid().ToString("N"));
            if (staleHeartbeat)
            {
                SetNestedField(journal, "HeartbeatUtcTicks", DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(2)).Ticks);
            }
            else
            {
                SetNestedField(journal, "ProcessId", 0);
                SetNestedField(journal, "ProcessStartUtcTicks", 0L);
            }
        });
        SimulateHardCrash(gameObject, recorder);
        RecreateRecorderAfterCrash();

        LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Deferring ambiguous active checkpoint owner"));
        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.False);
        Assert.That(Directory.GetFiles(
            Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery"),
            "pending_rows.*.journal"), Has.Length.EqualTo(1));
        Assert.That(File.ReadAllText(EventPath), Does.Not.Contain(",process_interrupted"));
    }

    [Test]
    public void DestroyingActiveRecorderFinalizesStartedSession()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        Invoke("RecordDroneInformed", 6, Activator.CreateInstance(cellType), 1);

        InvokeNonPublic("OnDestroy");
        UnityEngine.Object.DestroyImmediate(gameObject);

        string[] eventLines = File.ReadAllLines(EventPath);
        Assert.That(eventLines.Count(line => line.Contains(",drone_informed,6,,1,")), Is.EqualTo(1));
        Assert.That(eventLines.Count(line => line.Contains(",session_end,")), Is.EqualTo(1));
        Assert.That(eventLines.Single(line => line.Contains(",session_end,")), Does.EndWith(",recorder_destroyed"));
        string[] summaryLines = File.ReadAllLines(SummaryPath);
        Assert.That(summaryLines, Has.Length.EqualTo(2));
        Assert.That(summaryLines[1], Does.Contain(",recorder_destroyed,false,"));
    }

    [Test]
    public void DestroyAfterBootstrapFinalizationDoesNotDuplicateFinalRows()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        Assert.That((bool)Invoke("TryEndSession", "bootstrap_destroyed", false), Is.True);

        InvokeNonPublic("OnDestroy");
        UnityEngine.Object.DestroyImmediate(gameObject);

        Assert.That(File.ReadAllLines(EventPath).Count(line => line.Contains(",session_end,")), Is.EqualTo(1));
        string[] summaryLines = File.ReadAllLines(SummaryPath);
        Assert.That(summaryLines, Has.Length.EqualTo(2));
        Assert.That(summaryLines[1], Does.Contain(",bootstrap_destroyed,false,"));
        Assert.That(summaryLines[1], Does.Not.Contain("recorder_destroyed"));
    }

    [Test]
    public void DestroyWithUnwritableCsvReplaysFrozenFinalRowsOnRecreation()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        Invoke("RecordDroneInformed", 7, Activator.CreateInstance(cellType), 1);
        string originalEvents = File.ReadAllText(EventPath);
        File.Delete(EventPath);
        Directory.CreateDirectory(EventPath);

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        InvokeNonPublic("OnDestroy");
        UnityEngine.Object.DestroyImmediate(gameObject);
        string recoveryDirectory = Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery");
        Assert.That(Directory.GetFiles(recoveryDirectory, "pending_rows.*.journal"), Has.Length.EqualTo(1));

        Directory.Delete(EventPath);
        File.WriteAllText(EventPath, originalEvents);
        gameObject = new GameObject("telemetry-recorder-destroy-recreated-test");
        recorder = gameObject.AddComponent(recorderType);
        SetField("outputDirectoryName", Path.GetRelativePath(Application.persistentDataPath, outputDirectory));
        SetField("logOutputPathOnSessionStart", false);

        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        Assert.That(File.ReadAllLines(EventPath).Count(line => line.Contains(",drone_informed,7,,1,")), Is.EqualTo(1));
        Assert.That(File.ReadAllLines(EventPath).Count(line => line.Contains(",session_end,")), Is.EqualTo(1));
        Assert.That(File.ReadAllLines(EventPath).Single(line => line.Contains(",session_end,")),
            Does.EndWith(",recorder_destroyed"));
        Assert.That(File.ReadAllText(GetProperty<string>("SummaryFilePath")), Does.Contain(",recorder_destroyed,false,"));
        Assert.That(Directory.GetFiles(recoveryDirectory, "pending_rows.*.journal"), Is.Empty);
    }

    [Test]
    public void DestroyedRecorderReplaysFrozenEndAndSummaryRows()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string summaryPath = GetProperty<string>("SummaryFilePath");
        string originalSummary = File.ReadAllText(summaryPath);
        File.Delete(summaryPath);
        Directory.CreateDirectory(summaryPath);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        Assert.That(End(), Is.False);

        UnityEngine.Object.DestroyImmediate(gameObject);
        Directory.Delete(summaryPath);
        File.WriteAllText(summaryPath, originalSummary);
        gameObject = new GameObject("telemetry-recorder-final-recreated-test");
        recorder = gameObject.AddComponent(recorderType);
        SetField("outputDirectoryName", Path.GetRelativePath(Application.persistentDataPath, outputDirectory));
        SetField("logOutputPathOnSessionStart", false);

        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        Assert.That(File.ReadAllLines(EventPath).Count(line => line.Contains(",session_end,")), Is.EqualTo(1));
        Assert.That(File.ReadAllLines(summaryPath), Has.Length.EqualTo(2));
    }

    [Test]
    public void DestroyedRecorderReplaysPendingOrdinaryRowOnRecreation()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string original = File.ReadAllText(EventPath);
        File.Delete(EventPath);
        Directory.CreateDirectory(EventPath);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        Invoke("RecordDroneInformed", 7, Activator.CreateInstance(cellType), 1);

        UnityEngine.Object.DestroyImmediate(gameObject);
        Directory.Delete(EventPath);
        File.WriteAllText(EventPath, original);
        gameObject = new GameObject("telemetry-recorder-recreated-test");
        recorder = gameObject.AddComponent(recorderType);
        SetField("outputDirectoryName", Path.GetRelativePath(Application.persistentDataPath, outputDirectory));
        SetField("logOutputPathOnSessionStart", false);

        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        Assert.That(File.ReadAllLines(EventPath).Count(line => line.Contains(",drone_informed,7,,1,")), Is.EqualTo(1));
    }

    [Test]
    public void RecoveryAfterAmbiguousAppendDoesNotDuplicateRow()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string original = File.ReadAllText(EventPath);
        File.Delete(EventPath);
        Directory.CreateDirectory(EventPath);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        Invoke("RecordDroneInformed", 8, Activator.CreateInstance(cellType), 1);
        object queue = GetField("pendingOrdinaryEvents");
        object pending = queue.GetType().GetMethod("Peek").Invoke(queue, null);
        string[] row = (string[])pending.GetType().GetField("Row").GetValue(pending);
        MethodInfo toCsv = recorderType.GetMethod("ToCsvLine", BindingFlags.Static | BindingFlags.NonPublic);
        UnityEngine.Object.DestroyImmediate(gameObject);
        Directory.Delete(EventPath);
        File.WriteAllText(EventPath, original + (string)toCsv.Invoke(null, new object[] { row }) + Environment.NewLine);

        gameObject = new GameObject("telemetry-recorder-ambiguous-recreated-test");
        recorder = gameObject.AddComponent(recorderType);
        SetField("outputDirectoryName", Path.GetRelativePath(Application.persistentDataPath, outputDirectory));
        SetField("logOutputPathOnSessionStart", false);
        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        Assert.That(File.ReadAllLines(EventPath).Count(line => line.Contains(",drone_informed,8,,1,")), Is.EqualTo(1));
    }

    [Test]
    public void TwoConcurrentRecordersCrashIntoIndependentJournalsAndReplayBoth()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        var secondObject = new GameObject("telemetry-recorder-concurrent-test");
        Component second = secondObject.AddComponent(recorderType);
        SetFieldOn(second, "outputDirectoryName", Path.GetRelativePath(Application.persistentDataPath, outputDirectory));
        SetFieldOn(second, "logOutputPathOnSessionStart", false);
        Assert.That((bool)InvokeOn(second, "TryBeginSession", NewConfig()), Is.True);

        string original = File.ReadAllText(EventPath);
        File.Delete(EventPath);
        Directory.CreateDirectory(EventPath);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        Invoke("RecordDroneInformed", 31, Activator.CreateInstance(cellType), 1);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        InvokeOn(second, "RecordDroneInformed", 32, Activator.CreateInstance(cellType), 1);

        UnityEngine.Object.DestroyImmediate(gameObject);
        UnityEngine.Object.DestroyImmediate(secondObject);
        Directory.Delete(EventPath);
        File.WriteAllText(EventPath, original);
        string recoveryDirectory = Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery");
        Assert.That(Directory.GetFiles(recoveryDirectory, "pending_rows.*.journal"), Has.Length.EqualTo(2));

        gameObject = new GameObject("telemetry-recorder-concurrent-restart-test");
        recorder = gameObject.AddComponent(recorderType);
        SetField("outputDirectoryName", Path.GetRelativePath(Application.persistentDataPath, outputDirectory));
        SetField("logOutputPathOnSessionStart", false);
        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        Assert.That(File.ReadAllLines(EventPath).Count(line => line.Contains(",drone_informed,31,,1,")), Is.EqualTo(1));
        Assert.That(File.ReadAllLines(EventPath).Count(line => line.Contains(",drone_informed,32,,1,")), Is.EqualTo(1));
        Assert.That(Directory.GetFiles(recoveryDirectory, "pending_rows.*.journal"), Is.Empty);
    }

    [Test]
    public void JournalSurvivesFailedRestartAndReplaysOnLaterRestart()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string original = File.ReadAllText(EventPath);
        File.Delete(EventPath);
        Directory.CreateDirectory(EventPath);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        Invoke("RecordDroneInformed", 41, Activator.CreateInstance(cellType), 1);
        UnityEngine.Object.DestroyImmediate(gameObject);

        gameObject = new GameObject("telemetry-recorder-first-restart-test");
        recorder = gameObject.AddComponent(recorderType);
        SetField("outputDirectoryName", Path.GetRelativePath(Application.persistentDataPath, outputDirectory));
        SetField("logOutputPathOnSessionStart", false);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.False);
        string recoveryDirectory = Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery");
        Assert.That(Directory.GetFiles(recoveryDirectory, "pending_rows.*.journal"), Has.Length.EqualTo(1));
        UnityEngine.Object.DestroyImmediate(gameObject);

        Directory.Delete(EventPath);
        File.WriteAllText(EventPath, original);
        gameObject = new GameObject("telemetry-recorder-second-restart-test");
        recorder = gameObject.AddComponent(recorderType);
        SetField("outputDirectoryName", Path.GetRelativePath(Application.persistentDataPath, outputDirectory));
        SetField("logOutputPathOnSessionStart", false);
        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        Assert.That(File.ReadAllLines(EventPath).Count(line => line.Contains(",drone_informed,41,,1,")), Is.EqualTo(1));
        Assert.That(Directory.GetFiles(recoveryDirectory, "pending_rows.*.journal"), Is.Empty);
    }

    [Test]
    public void OneRestartReplaysDeadOwnersToMultipleRecordedDestinations()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string firstOutput = outputDirectory;
        string firstOriginal = File.ReadAllText(EventPath);

        string secondOutput = Path.Combine(Application.persistentDataPath, "DroneTelemetryTests", Guid.NewGuid().ToString("N"));
        var secondObject = new GameObject("telemetry-recorder-other-owner-test");
        Component second = secondObject.AddComponent(recorderType);
        SetFieldOn(second, "outputDirectoryName", Path.GetRelativePath(Application.persistentDataPath, secondOutput));
        SetFieldOn(second, "logOutputPathOnSessionStart", false);
        Assert.That((bool)InvokeOn(second, "TryBeginSession", NewConfig()), Is.True);
        string secondEvent = Path.Combine(secondOutput, "session_events.csv");
        string secondOriginal = File.ReadAllText(secondEvent);

        File.Delete(EventPath);
        Directory.CreateDirectory(EventPath);
        File.Delete(secondEvent);
        Directory.CreateDirectory(secondEvent);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        Invoke("RecordDroneInformed", 51, Activator.CreateInstance(cellType), 1);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        InvokeOn(second, "RecordDroneInformed", 52, Activator.CreateInstance(cellType), 1);
        UnityEngine.Object.DestroyImmediate(gameObject);
        UnityEngine.Object.DestroyImmediate(secondObject);
        Directory.Delete(EventPath);
        File.WriteAllText(EventPath, firstOriginal);
        Directory.Delete(secondEvent);
        File.WriteAllText(secondEvent, secondOriginal);

        gameObject = new GameObject("telemetry-recorder-owner-restart-test");
        recorder = gameObject.AddComponent(recorderType);
        SetField("outputDirectoryName", Path.GetRelativePath(Application.persistentDataPath, firstOutput));
        SetField("logOutputPathOnSessionStart", false);
        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        string recoveryDirectory = Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery");
        Assert.That(Directory.GetFiles(recoveryDirectory, "pending_rows.*.journal"), Is.Empty);
        Assert.That(File.ReadAllText(EventPath), Does.Contain(",drone_informed,51,,1,"));
        Assert.That(File.ReadAllText(secondEvent), Does.Contain(",drone_informed,52,,1,"));
        if (Directory.Exists(secondOutput)) Directory.Delete(secondOutput, true);
    }

    [Test]
    public void ValidMainJournalReplaysWhenNewerTempIsCorrupt()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string original = File.ReadAllText(EventPath);
        File.Delete(EventPath);
        Directory.CreateDirectory(EventPath);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        Invoke("RecordDroneInformed", 61, Activator.CreateInstance(cellType), 1);
        Directory.Delete(EventPath);
        File.WriteAllText(EventPath, original);
        RecreateRecorderForRecovery();

        string recoveryDirectory = Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery");
        string journalPath = Directory.GetFiles(recoveryDirectory, "pending_rows.*.journal").Single();
        string tempPath = journalPath + ".tmp";
        byte[] corrupt = { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(tempPath, corrupt);
        File.SetLastWriteTimeUtc(journalPath, DateTime.UtcNow.AddMinutes(-1));
        File.SetLastWriteTimeUtc(tempPath, DateTime.UtcNow);
        LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Quarantined corrupt recovery artifact"));

        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        Assert.That(File.ReadAllLines(EventPath).Count(line => line.Contains(",drone_informed,61,,1,")), Is.EqualTo(1));
        Assert.That(File.Exists(journalPath), Is.False);
        Assert.That(File.Exists(tempPath), Is.False);
        string quarantined = Directory.GetFiles(recoveryDirectory, Path.GetFileName(tempPath) + ".corrupt*").Single();
        CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(quarantined));
    }

    [Test]
    public void ValidBackupJournalReplaysWhenNewerMainIsCorrupt()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string original = File.ReadAllText(EventPath);
        File.Delete(EventPath);
        Directory.CreateDirectory(EventPath);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        Invoke("RecordDroneInformed", 62, Activator.CreateInstance(cellType), 1);
        Directory.Delete(EventPath);
        File.WriteAllText(EventPath, original);
        RecreateRecorderForRecovery();

        string recoveryDirectory = Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery");
        string journalPath = Directory.GetFiles(recoveryDirectory, "pending_rows.*.journal").Single();
        string backupPath = journalPath + ".bak";
        File.Move(journalPath, backupPath);
        byte[] corrupt = { 6, 7, 8 };
        File.WriteAllBytes(journalPath, corrupt);
        File.SetLastWriteTimeUtc(backupPath, DateTime.UtcNow.AddMinutes(-1));
        File.SetLastWriteTimeUtc(journalPath, DateTime.UtcNow);
        LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Quarantined corrupt recovery artifact"));

        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        Assert.That(File.ReadAllLines(EventPath).Count(line => line.Contains(",drone_informed,62,,1,")), Is.EqualTo(1));
        Assert.That(File.Exists(backupPath), Is.False);
        Assert.That(File.Exists(journalPath), Is.False);
        string quarantined = Directory.GetFiles(recoveryDirectory, Path.GetFileName(journalPath) + ".corrupt*").Single();
        CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(quarantined));
    }

    [Test]
    public void AllCorruptJournalArtifactsFailReadinessWithoutDeletingEvidence()
    {
        string recoveryDirectory = Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery");
        Directory.CreateDirectory(recoveryDirectory);
        string journalPath = Path.Combine(recoveryDirectory, "pending_rows." + Guid.NewGuid().ToString("N") + ".journal");
        byte[][] corrupt =
        {
            new byte[] { 1, 2, 3, 4, 5 },
            new byte[] { 6, 7, 8 },
            new byte[] { 9 }
        };
        string[] artifacts = { journalPath, journalPath + ".tmp", journalPath + ".bak" };
        for (int i = 0; i < artifacts.Length; i++) File.WriteAllBytes(artifacts[i], corrupt[i]);
        LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("No valid recovery journal artifact exists"));

        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.False);
        for (int i = 0; i < artifacts.Length; i++)
        {
            Assert.That(File.Exists(artifacts[i]), Is.True);
            CollectionAssert.AreEqual(corrupt[i], File.ReadAllBytes(artifacts[i]));
        }
    }

    [Test]
    public void RecoveryJournalReplaysToOriginalDestinationAfterOutputPathChanges()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string originalOutput = outputDirectory;
        string originalEventPath = EventPath;
        string original = File.ReadAllText(originalEventPath);
        File.Delete(originalEventPath);
        Directory.CreateDirectory(originalEventPath);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        Invoke("RecordDroneInformed", 9, Activator.CreateInstance(cellType), 1);
        UnityEngine.Object.DestroyImmediate(gameObject);
        Directory.Delete(originalEventPath);
        File.WriteAllText(originalEventPath, original);

        gameObject = new GameObject("telemetry-recorder-path-change-test");
        recorder = gameObject.AddComponent(recorderType);
        SetField("outputDirectoryName", Path.Combine("DroneTelemetryTests", Guid.NewGuid().ToString("N")));
        outputDirectory = GetProperty<string>("OutputDirectory");

        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        Assert.That(File.ReadAllText(originalEventPath), Does.Contain(",drone_informed,9,,1,"));
        Assert.That(Directory.GetFiles(
            Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery"),
            "pending_rows.*.journal"), Is.Empty);
        if (Directory.Exists(originalOutput)) Directory.Delete(originalOutput, true);
    }

    [Test]
    public void RecoveryJournalReplaysOriginalOutputsAfterCurrentOutputsAreDisabled()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string originalEventPath = EventPath;
        string original = File.ReadAllText(originalEventPath);
        File.Delete(originalEventPath);
        Directory.CreateDirectory(originalEventPath);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to append schema"));
        Invoke("RecordDroneInformed", 10, Activator.CreateInstance(cellType), 1);
        UnityEngine.Object.DestroyImmediate(gameObject);
        Directory.Delete(originalEventPath);
        File.WriteAllText(originalEventPath, original);

        gameObject = new GameObject("telemetry-recorder-disabled-output-recovery-test");
        recorder = gameObject.AddComponent(recorderType);
        SetField("writeEventCsv", false);
        SetField("writeSummaryCsv", false);
        SetField("outputDirectoryName", Path.GetRelativePath(Application.persistentDataPath, outputDirectory));

        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.True);
        Assert.That(File.ReadAllText(originalEventPath), Does.Contain(",drone_informed,10,,1,"));
        Assert.That(Directory.GetFiles(
            Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery"),
            "pending_rows.*.journal"), Is.Empty);
    }

    [Test]
    public void UnsafeRecordedRecoveryPathFailsReadinessAndPreservesEvidence()
    {
        Assert.That(Begin(NewConfig()), Is.True);
        string unsafePath = Path.Combine(Path.GetTempPath(), "telemetry-unsafe-" + Guid.NewGuid().ToString("N") + ".csv");
        RewriteActiveJournalOwner(journal => SetNestedField(journal, "EventPath", unsafePath));
        SimulateHardCrash(gameObject, recorder);
        RecreateRecorderAfterCrash();

        string recoveryPath = Directory.GetFiles(
            Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery"),
            "pending_rows.*.journal").Single();
        byte[] evidence = File.ReadAllBytes(recoveryPath);
        LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Unsafe event recovery destination"));

        Assert.That((bool)Invoke("TryRevalidateStorage"), Is.False);
        Assert.That(File.Exists(unsafePath), Is.False);
        CollectionAssert.AreEqual(evidence, File.ReadAllBytes(recoveryPath));
    }

    private void RewriteActiveJournalOwner(Action<object> mutate)
    {
        string journalPath = Directory.GetFiles(
            Path.Combine(Application.persistentDataPath, "DroneTelemetryRecovery"),
            "pending_rows.*.journal").Single();
        MethodInfo load = recorderType.GetMethod(
            "LoadNewestJournalArtifact", BindingFlags.Instance | BindingFlags.NonPublic);
        object journal = load.Invoke(recorder, new object[] { journalPath });
        mutate(journal);
        MethodInfo write = recorderType.GetMethod(
            "WriteRecoveryJournalAtomically", BindingFlags.Static | BindingFlags.NonPublic);
        write.Invoke(null, new[] { journalPath, journal });
    }

    private void SetMachineIdentityOverride(string identity)
    {
        recorderType?.GetField(
            "machineIdentityOverride", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, identity);
    }

    private void SetProcessProbeOverride(Func<int, long> probe)
    {
        recorderType?.GetField(
            "processStartUtcTicksProbeOverride", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, probe);
    }

    private static void SetNestedField(object target, string name, object value)
    {
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public).SetValue(target, value);
    }

    private void AppendSummaryRow(string sessionId, string batchId, string endReason)
    {
        MethodInfo append = recorderType.GetMethod(
            "AppendCsvRow", BindingFlags.Instance | BindingFlags.NonPublic);
        string[] header = (string[])recorderType.GetField(
            "SummaryHeader", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var row = new string[header.Length];
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = string.Empty;
        }
        row[0] = sessionId;
        row[1] = batchId;
        row[7] = "2026-01-01T00:00:00.0000000Z";
        row[8] = endReason;
        row[9] = string.Equals(endReason, "mission_complete", StringComparison.Ordinal) ? "true" : "false";
        Assert.That((bool)append.Invoke(
            recorder,
            new object[] { GetProperty<string>("SummaryFilePath"), header, row, false, false, null }),
            Is.True);
    }

    private string ToSummaryCsvLine(string sessionId, string batchId, string endReason)
    {
        string[] header = (string[])recorderType.GetField(
            "SummaryHeader", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var row = new string[header.Length];
        for (int i = 0; i < row.Length; i++)
        {
            row[i] = string.Empty;
        }
        row[0] = sessionId;
        row[1] = batchId;
        row[7] = "2026-01-01T00:00:00.0000000Z";
        row[8] = endReason;
        MethodInfo toCsvLine = recorderType.GetMethod("ToCsvLine", BindingFlags.Static | BindingFlags.NonPublic);
        return (string)toCsvLine.Invoke(null, new object[] { row });
    }

    private void SimulateHardCrash(GameObject crashedObject, Component crashedRecorder)
    {
        // Do not run graceful finalization. OnDestroy unregisters the in-process
        // owner, modelling the next startup while leaving its durable checkpoint.
        SetFieldOn(crashedRecorder, "activeSession", false);
        UnityEngine.Object.DestroyImmediate(crashedObject);
    }

    private void RecreateRecorderAfterCrash()
    {
        gameObject = new GameObject("telemetry-recorder-hard-crash-recreation-test");
        recorder = gameObject.AddComponent(recorderType);
        SetField("outputDirectoryName", Path.GetRelativePath(Application.persistentDataPath, outputDirectory));
        SetField("logOutputPathOnSessionStart", false);
    }

    private void RecreateRecorderForRecovery()
    {
        // Leave the already durable pending snapshot behind without asking
        // OnDestroy to freeze additional session-end rows.
        SetField("activeSession", false);
        UnityEngine.Object.DestroyImmediate(gameObject);
        gameObject = new GameObject("telemetry-recorder-artifact-recovery-test");
        recorder = gameObject.AddComponent(recorderType);
        SetField("outputDirectoryName", Path.GetRelativePath(Application.persistentDataPath, outputDirectory));
        SetField("logOutputPathOnSessionStart", false);
    }

    private string EventPath => Path.Combine(outputDirectory, "session_events.csv");
    private string SummaryPath => Path.Combine(outputDirectory, "session_summary.csv");
    private object NewConfig() => Activator.CreateInstance(configType);
    private bool Begin(object config) => (bool)Invoke("TryBeginSession", config);
    private bool End() => (bool)Invoke("TryEndSession", "complete", true);

    private object Invoke(string name, params object[] arguments)
    {
        MethodInfo method = recorderType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.That(method, Is.Not.Null, $"Missing method {name}");
        return method.Invoke(recorder, arguments);
    }

    private void InvokeNonPublic(string name)
    {
        MethodInfo method = recorderType.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Missing method {name}");
        method.Invoke(recorder, null);
    }

    private T GetProperty<T>(string name)
    {
        PropertyInfo property = recorderType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.That(property, Is.Not.Null, $"Missing property {name}");
        return (T)property.GetValue(recorder);
    }

    private object GetField(string name)
    {
        FieldInfo field = recorderType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field {name}");
        return field.GetValue(recorder);
    }

    private void SetField(string name, object value)
    {
        SetFieldOn(recorder, name, value);
    }

    private void SetFieldOn(Component target, string name, object value)
    {
        FieldInfo field = recorderType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field {name}");
        field.SetValue(target, value);
    }

    private object InvokeOn(Component target, string name, params object[] arguments)
    {
        MethodInfo method = recorderType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.That(method, Is.Not.Null, $"Missing method {name}");
        return method.Invoke(target, arguments);
    }

    private static Type RequireType(string assemblyQualifiedName)
    {
        Type type = Type.GetType(assemblyQualifiedName);
        Assert.That(type, Is.Not.Null, $"Missing type {assemblyQualifiedName}");
        return type;
    }
}
