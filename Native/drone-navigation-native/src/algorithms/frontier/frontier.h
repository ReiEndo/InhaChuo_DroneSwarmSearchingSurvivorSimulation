#pragma once

#include "../../core/path.h"

#include <cstdint>
#include <optional>
#include <vector>

namespace drone::algorithms::frontier {

struct FrontierSearchRequest {
  GridBounds bounds;
  Vec3i start;
  std::vector<CellStateUpdate> discovered_cells;
};

struct ReachableFrontier {
  Vec3i target;
  PathResult path;
};

struct FrontierScoringSettings {
  float travel_cost_weight = 0.0F;
  float information_gain_weight = 0.0F;
  int information_gain_radius = 0;
  float recent_goal_penalty = 0.0F;
  float same_goal_penalty = 0.0F;
  float nearby_drone_penalty_radius = 0.0F;
};

struct FrontierRankingRequest {
  GridBounds bounds;
  Vec3i start;
  std::vector<CellStateUpdate> discovered_cells;
  std::vector<Vec3i> recent_goal_cells;
  std::vector<Vec3i> occupied_goal_cells;
  FrontierScoringSettings settings;
};

struct RankedFrontierCandidate {
  Vec3i cell;
  float score = 0.0F;
  float raw_distance_squared = 0.0F;
};

/**
 * Returns known free frontier cells sorted by Manhattan distance from start.
 *
 * A frontier is a known free cell that is adjacent to at least one unknown cell
 * in the 6-connected 3D grid. Cells outside bounds are ignored, not treated as
 * unknown.
 */
[[nodiscard]] std::vector<Vec3i>
FindFrontiers(const FrontierSearchRequest &request);

/**
 * Returns the nearest frontier that the supplied planner can route to.
 */
[[nodiscard]] std::optional<ReachableFrontier>
FindNearestReachableFrontier(const IPathPlanner &planner,
                             const FrontierSearchRequest &request);

[[nodiscard]] std::vector<RankedFrontierCandidate>
RankFrontierCandidates(const FrontierRankingRequest &request);

} // namespace drone::algorithms::frontier

extern "C" {

struct DroneFrontierScoringSettings {
  float travel_cost_weight;
  float information_gain_weight;
  std::int32_t information_gain_radius;
  float recent_goal_penalty;
  float same_goal_penalty;
  float nearby_drone_penalty_radius;
};

struct DroneFrontierCandidate {
  DroneVec3i cell;
  float score;
  float raw_distance_squared;
};

std::int32_t DroneRankFrontierCandidates(
    std::int32_t width, std::int32_t height, std::int32_t depth,
    DroneVec3i start, const DroneVec3i *known_cells,
    const std::int32_t *known_states, std::int32_t known_count,
    const DroneVec3i *recent_goal_cells, std::int32_t recent_goal_count,
    const DroneVec3i *occupied_goal_cells, std::int32_t occupied_goal_count,
    DroneFrontierScoringSettings settings,
    DroneFrontierCandidate *out_candidates, std::int32_t out_capacity);

} // extern "C"
