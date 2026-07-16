using System;
using System.Runtime.InteropServices;

public static class DroneNative
{
#if UNITY_EDITOR_WIN || UNITY_EDITOR_LINUX || UNITY_EDITOR_OSX || UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX
    private const string LibName = "drone_algo";
#endif

    private const string UnsupportedPlatformMessage =
        "Drone native algorithms are supported only on Windows x86-64, Linux x86-64, and macOS ARM64.";

    [StructLayout(LayoutKind.Sequential)]
    public struct DroneVec3i
    {
        public int x;
        public int y;
        public int z;

        public DroneVec3i(int x, int y, int z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DroneVec3f
    {
        public float x;
        public float y;
        public float z;

        public DroneVec3f(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DroneNeighborState
    {
        public DroneVec3f position;
        public DroneVec3f velocity;
        public float radius;

        public DroneNeighborState(DroneVec3f position, DroneVec3f velocity, float radius)
        {
            this.position = position;
            this.velocity = velocity;
            this.radius = radius;
        }
    }

    public enum PlannerType : int
    {
        AStar = 0,
        ThetaStar = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DroneFrontierScoringSettings
    {
        public float travel_cost_weight;
        public float information_gain_weight;
        public int information_gain_radius;
        public float recent_goal_penalty;
        public float same_goal_penalty;
        public float nearby_drone_penalty_radius;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DroneFrontierCandidate
    {
        public DroneVec3i cell;
        public float score;
        public float raw_distance_squared;
    }

    public static int ToNativeCellState(DroneCellState state)
    {
        return state switch
        {
            DroneCellState.Unknown => 0,
            DroneCellState.Free => 1,
            DroneCellState.Blocked => 2,
            DroneCellState.Target => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown drone cell state.")
        };
    }

    public static int DronePlanKnownPath(
        int plannerType,
        int width,
        int height,
        int depth,
        DroneVec3i start,
        DroneVec3i goal,
        DroneVec3i[] knownCells,
        int[] knownStates,
        int knownCount,
        DroneVec3i[] outPath,
        int outCapacity
    )
    {
        EnsureSupportedPlatform();
        ValidatePlannerInput(plannerType, width, height, depth, knownCells, knownStates, knownCount, outPath, outCapacity);

        return DronePlanKnownPathNative(
            plannerType,
            width,
            height,
            depth,
            start,
            goal,
            knownCells,
            knownStates,
            knownCount,
            outPath,
            outCapacity
        );
    }

    public static int DroneComputeLocalAvoidanceVelocity(
        DroneVec3f selfPosition,
        DroneVec3f preferredVelocity,
        float selfRadius,
        float maxSpeed,
        float timeHorizonSeconds,
        DroneNeighborState[] neighbors,
        int neighborCount,
        out DroneVec3f outVelocity
    )
    {
        EnsureSupportedPlatform();
        ValidateAvoidanceInput(selfRadius, maxSpeed, timeHorizonSeconds, neighbors, neighborCount);

        return DroneComputeLocalAvoidanceVelocityNative(
            selfPosition,
            preferredVelocity,
            selfRadius,
            maxSpeed,
            timeHorizonSeconds,
            neighbors,
            neighborCount,
            out outVelocity
        );
    }

    public static int DroneRankFrontierCandidates(
        int width,
        int height,
        int depth,
        DroneVec3i start,
        DroneVec3i[] knownCells,
        int[] knownStates,
        int knownCount,
        DroneVec3i[] recentGoalCells,
        int recentGoalCount,
        DroneVec3i[] occupiedGoalCells,
        int occupiedGoalCount,
        DroneFrontierScoringSettings settings,
        DroneFrontierCandidate[] outCandidates,
        int outCapacity
    )
    {
        EnsureSupportedPlatform();
        ValidateFrontierRankingInput(
            width,
            height,
            depth,
            knownCells,
            knownStates,
            knownCount,
            recentGoalCells,
            recentGoalCount,
            occupiedGoalCells,
            occupiedGoalCount,
            settings,
            outCandidates,
            outCapacity
        );

        return DroneRankFrontierCandidatesNative(
            width,
            height,
            depth,
            start,
            knownCells,
            knownStates,
            knownCount,
            recentGoalCells,
            recentGoalCount,
            occupiedGoalCells,
            occupiedGoalCount,
            settings,
            outCandidates,
            outCapacity
        );
    }

    private static void ValidatePlannerInput(
        int plannerType,
        int width,
        int height,
        int depth,
        DroneVec3i[] knownCells,
        int[] knownStates,
        int knownCount,
        DroneVec3i[] outPath,
        int outCapacity
    )
    {
        if (!Enum.IsDefined(typeof(PlannerType), plannerType))
        {
            throw new ArgumentOutOfRangeException(nameof(plannerType), plannerType, "Unknown planner type.");
        }

        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        if (depth <= 0) throw new ArgumentOutOfRangeException(nameof(depth), depth, "Depth must be positive.");
        if (knownCells == null) throw new ArgumentNullException(nameof(knownCells));
        if (knownStates == null) throw new ArgumentNullException(nameof(knownStates));
        if (outPath == null) throw new ArgumentNullException(nameof(outPath));
        if (knownCount < 0) throw new ArgumentOutOfRangeException(nameof(knownCount), knownCount, "Known count cannot be negative.");
        if (knownCount > knownCells.Length) throw new ArgumentException("Known count exceeds known cell array length.", nameof(knownCount));
        if (knownCount > knownStates.Length) throw new ArgumentException("Known count exceeds known state array length.", nameof(knownCount));
        if (outCapacity < 0) throw new ArgumentOutOfRangeException(nameof(outCapacity), outCapacity, "Output capacity cannot be negative.");
        if (outCapacity > outPath.Length) throw new ArgumentException("Output capacity exceeds output path array length.", nameof(outCapacity));
    }

    private static void ValidateAvoidanceInput(
        float selfRadius,
        float maxSpeed,
        float timeHorizonSeconds,
        DroneNeighborState[] neighbors,
        int neighborCount
    )
    {
        if (neighbors == null) throw new ArgumentNullException(nameof(neighbors));
        if (neighborCount < 0) throw new ArgumentOutOfRangeException(nameof(neighborCount), neighborCount, "Neighbor count cannot be negative.");
        if (neighborCount > neighbors.Length) throw new ArgumentException("Neighbor count exceeds neighbor array length.", nameof(neighborCount));
        if (!IsFinite(selfRadius) || selfRadius < 0f) throw new ArgumentOutOfRangeException(nameof(selfRadius), selfRadius, "Self radius must be finite and non-negative.");
        if (!IsFinite(maxSpeed) || maxSpeed < 0f) throw new ArgumentOutOfRangeException(nameof(maxSpeed), maxSpeed, "Max speed must be finite and non-negative.");
        if (!IsFinite(timeHorizonSeconds) || timeHorizonSeconds <= 0f) throw new ArgumentOutOfRangeException(nameof(timeHorizonSeconds), timeHorizonSeconds, "Time horizon must be finite and positive.");
    }

    private static void ValidateFrontierRankingInput(
        int width,
        int height,
        int depth,
        DroneVec3i[] knownCells,
        int[] knownStates,
        int knownCount,
        DroneVec3i[] recentGoalCells,
        int recentGoalCount,
        DroneVec3i[] occupiedGoalCells,
        int occupiedGoalCount,
        DroneFrontierScoringSettings settings,
        DroneFrontierCandidate[] outCandidates,
        int outCapacity
    )
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        if (depth <= 0) throw new ArgumentOutOfRangeException(nameof(depth), depth, "Depth must be positive.");
        if (knownCount < 0) throw new ArgumentOutOfRangeException(nameof(knownCount), knownCount, "Known count cannot be negative.");
        if (recentGoalCount < 0) throw new ArgumentOutOfRangeException(nameof(recentGoalCount), recentGoalCount, "Recent goal count cannot be negative.");
        if (occupiedGoalCount < 0) throw new ArgumentOutOfRangeException(nameof(occupiedGoalCount), occupiedGoalCount, "Occupied goal count cannot be negative.");
        if (knownCount > 0 && knownCells == null) throw new ArgumentNullException(nameof(knownCells));
        if (knownCount > 0 && knownStates == null) throw new ArgumentNullException(nameof(knownStates));
        if (recentGoalCount > 0 && recentGoalCells == null) throw new ArgumentNullException(nameof(recentGoalCells));
        if (occupiedGoalCount > 0 && occupiedGoalCells == null) throw new ArgumentNullException(nameof(occupiedGoalCells));
        if (knownCells != null && knownCount > knownCells.Length) throw new ArgumentException("Known count exceeds known cell array length.", nameof(knownCount));
        if (knownStates != null && knownCount > knownStates.Length) throw new ArgumentException("Known count exceeds known state array length.", nameof(knownCount));
        if (recentGoalCells != null && recentGoalCount > recentGoalCells.Length) throw new ArgumentException("Recent goal count exceeds recent goal array length.", nameof(recentGoalCount));
        if (occupiedGoalCells != null && occupiedGoalCount > occupiedGoalCells.Length) throw new ArgumentException("Occupied goal count exceeds occupied goal array length.", nameof(occupiedGoalCount));
        if (outCapacity < 0) throw new ArgumentOutOfRangeException(nameof(outCapacity), outCapacity, "Output capacity cannot be negative.");
        if (outCapacity > 0 && outCandidates == null) throw new ArgumentNullException(nameof(outCandidates));
        if (outCandidates != null && outCapacity > outCandidates.Length) throw new ArgumentException("Output capacity exceeds output candidate array length.", nameof(outCapacity));
        ValidateFrontierSettings(settings);
    }

    private static void ValidateFrontierSettings(DroneFrontierScoringSettings settings)
    {
        if (!IsFinite(settings.travel_cost_weight) || settings.travel_cost_weight < 0f) throw new ArgumentOutOfRangeException(nameof(settings.travel_cost_weight), settings.travel_cost_weight, "Travel cost weight must be finite and non-negative.");
        if (!IsFinite(settings.information_gain_weight) || settings.information_gain_weight < 0f) throw new ArgumentOutOfRangeException(nameof(settings.information_gain_weight), settings.information_gain_weight, "Information gain weight must be finite and non-negative.");
        if (settings.information_gain_radius < 0) throw new ArgumentOutOfRangeException(nameof(settings.information_gain_radius), settings.information_gain_radius, "Information gain radius cannot be negative.");
        if (!IsFinite(settings.recent_goal_penalty) || settings.recent_goal_penalty < 0f) throw new ArgumentOutOfRangeException(nameof(settings.recent_goal_penalty), settings.recent_goal_penalty, "Recent goal penalty must be finite and non-negative.");
        if (!IsFinite(settings.same_goal_penalty) || settings.same_goal_penalty < 0f) throw new ArgumentOutOfRangeException(nameof(settings.same_goal_penalty), settings.same_goal_penalty, "Same goal penalty must be finite and non-negative.");
        if (!IsFinite(settings.nearby_drone_penalty_radius) || settings.nearby_drone_penalty_radius < 0f) throw new ArgumentOutOfRangeException(nameof(settings.nearby_drone_penalty_radius), settings.nearby_drone_penalty_radius, "Nearby drone penalty radius must be finite and non-negative.");
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static void EnsureSupportedPlatform()
    {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_LINUX
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return;
        }
#elif UNITY_EDITOR_OSX
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            return;
        }
#elif UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return;
        }
#elif UNITY_STANDALONE_OSX
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            return;
        }
#endif

        ThrowUnsupportedPlatform();
    }

    private static int ThrowUnsupportedPlatform()
    {
        throw new PlatformNotSupportedException(UnsupportedPlatformMessage);
    }

#if UNITY_EDITOR_WIN || UNITY_EDITOR_LINUX || UNITY_EDITOR_OSX || UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX
    [DllImport(LibName, EntryPoint = "DronePlanKnownPath", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DronePlanKnownPathNative(
        int plannerType,
        int width,
        int height,
        int depth,
        DroneVec3i start,
        DroneVec3i goal,
        [In] DroneVec3i[] knownCells,
        [In] int[] knownStates,
        int knownCount,
        [Out] DroneVec3i[] outPath,
        int outCapacity
    );

    [DllImport(LibName, EntryPoint = "DroneComputeLocalAvoidanceVelocity", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DroneComputeLocalAvoidanceVelocityNative(
        DroneVec3f selfPosition,
        DroneVec3f preferredVelocity,
        float selfRadius,
        float maxSpeed,
        float timeHorizonSeconds,
        [In] DroneNeighborState[] neighbors,
        int neighborCount,
        out DroneVec3f outVelocity
    );

    [DllImport(LibName, EntryPoint = "DroneRankFrontierCandidates", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DroneRankFrontierCandidatesNative(
        int width,
        int height,
        int depth,
        DroneVec3i start,
        [In] DroneVec3i[] knownCells,
        [In] int[] knownStates,
        int knownCount,
        [In] DroneVec3i[] recentGoalCells,
        int recentGoalCount,
        [In] DroneVec3i[] occupiedGoalCells,
        int occupiedGoalCount,
        DroneFrontierScoringSettings settings,
        [Out] DroneFrontierCandidate[] outCandidates,
        int outCapacity
    );
#else
    private static int DronePlanKnownPathNative(
        int plannerType,
        int width,
        int height,
        int depth,
        DroneVec3i start,
        DroneVec3i goal,
        DroneVec3i[] knownCells,
        int[] knownStates,
        int knownCount,
        DroneVec3i[] outPath,
        int outCapacity
    )
    {
        return ThrowUnsupportedPlatform();
    }

    private static int DroneComputeLocalAvoidanceVelocityNative(
        DroneVec3f selfPosition,
        DroneVec3f preferredVelocity,
        float selfRadius,
        float maxSpeed,
        float timeHorizonSeconds,
        DroneNeighborState[] neighbors,
        int neighborCount,
        out DroneVec3f outVelocity
    )
    {
        outVelocity = default;
        return ThrowUnsupportedPlatform();
    }

    private static int DroneRankFrontierCandidatesNative(
        int width,
        int height,
        int depth,
        DroneVec3i start,
        DroneVec3i[] knownCells,
        int[] knownStates,
        int knownCount,
        DroneVec3i[] recentGoalCells,
        int recentGoalCount,
        DroneVec3i[] occupiedGoalCells,
        int occupiedGoalCount,
        DroneFrontierScoringSettings settings,
        DroneFrontierCandidate[] outCandidates,
        int outCapacity
    )
    {
        return ThrowUnsupportedPlatform();
    }
#endif
}
