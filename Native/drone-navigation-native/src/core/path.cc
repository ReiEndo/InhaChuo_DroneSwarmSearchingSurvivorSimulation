#include "path.h"

#include "../algorithms/astar/planner.h"
#include "../algorithms/theta_star/planner.h"

#include <limits>

namespace drone {

bool GridBounds::Contains(const Vec3i &p) const {
  return p.x >= 0 && p.y >= 0 && p.z >= 0 && p.x < width && p.y < height &&
         p.z < depth;
}

int GridBounds::CellCount() const {
  if (width <= 0 || height <= 0 || depth <= 0)
    return 0;
  const long long count = static_cast<long long>(width) * height * depth;
  if (count > std::numeric_limits<int>::max())
    return 0;
  return static_cast<int>(count);
}

std::unique_ptr<IPathPlanner> CreatePathPlanner(PlannerType type) {
  switch (type) {
  case PlannerType::kAStar:
    return std::make_unique<algorithms::astar::Planner>();
  case PlannerType::kThetaStar:
    return std::make_unique<algorithms::theta_star::Planner>();
  }
  return nullptr;
}

PathResult PlanPath(const IPathPlanner &planner, const PathRequest &request) {
  return planner.Plan(request);
}

} // namespace drone

namespace {

bool IsValidDroneCellState(std::int32_t state) {
  return state == DRONE_CELL_UNKNOWN || state == DRONE_CELL_FREE ||
         state == DRONE_CELL_BLOCKED || state == DRONE_CELL_TARGET;
}

DroneVec3i ToDroneVec3i(const drone::Vec3i &point) {
  return {point.x, point.y, point.z};
}

std::int32_t WriteResult(const drone::PathResult &result, DroneVec3i *out_path,
                         std::int32_t out_capacity) {
  if (!result.success)
    return 0;

  const auto required = static_cast<std::int32_t>(result.points.size());
  if (out_path != nullptr && out_capacity >= required) {
    for (std::int32_t i = 0; i < required; ++i) {
      out_path[i] = ToDroneVec3i(result.points[i]);
    }
  }
  return required;
}

} // namespace

extern "C" std::int32_t
DronePlanKnownPath(std::int32_t planner_type, std::int32_t width,
                   std::int32_t height, std::int32_t depth, DroneVec3i start,
                   DroneVec3i goal, const DroneVec3i *known_cells,
                   const std::int32_t *known_states, std::int32_t known_count,
                   DroneVec3i *out_path, std::int32_t out_capacity) {
  if (known_count <= 0 || out_capacity < 0)
    return 0;
  if (known_cells == nullptr || known_states == nullptr) {
    return 0;
  }

  auto planner =
      drone::CreatePathPlanner(static_cast<drone::PlannerType>(planner_type));
  if (planner == nullptr)
    return 0;

  drone::PathRequest request{{width, height, depth},
                             {start.x, start.y, start.z},
                             {goal.x, goal.y, goal.z}};
  request.discovered_cells.reserve(static_cast<std::size_t>(known_count));
  for (std::int32_t i = 0; i < known_count; ++i) {
    if (!IsValidDroneCellState(known_states[i]))
      return 0;
    request.discovered_cells.push_back(
        {{known_cells[i].x, known_cells[i].y, known_cells[i].z},
         static_cast<drone::CellState>(known_states[i])});
  }

  return WriteResult(drone::PlanPath(*planner, request), out_path,
                     out_capacity);
}
