#pragma once

#include "../../core/path.h"

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

} // namespace drone::algorithms::frontier
