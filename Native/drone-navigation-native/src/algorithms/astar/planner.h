#pragma once

#include "../../core/path.h"

namespace drone::algorithms::astar {

class Planner final : public IPathPlanner {
public:
  /**
   * Plans a shortest path through discovered safe cells.
   *
   * Movement is 6-connected in the 3D grid, so each step changes exactly one
   * coordinate by one cell.
   */
  [[nodiscard]] PathResult Plan(const PathRequest &request) const override;
};

// Convenience wrapper for direct A* callers and tests
PathResult PlanPath(const PathRequest &request);

} // namespace drone::algorithms::astar
