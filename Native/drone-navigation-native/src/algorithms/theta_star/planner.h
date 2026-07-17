#pragma once

#include "../../core/path.h"

namespace drone::algorithms::theta_star {

class Planner final : public IPathPlanner {
public:
  /**
   * Plans an any-angle path through discovered safe cells.
   *
   * Expands local 26-connected neighbors, then rewires nodes to a
   * visible ancestor when the straight segment crosses only safe cells.
   */
  [[nodiscard]] PathResult Plan(const PathRequest &request) const override;
};

PathResult PlanPath(const PathRequest &request);

} // namespace drone::algorithms::theta_star
