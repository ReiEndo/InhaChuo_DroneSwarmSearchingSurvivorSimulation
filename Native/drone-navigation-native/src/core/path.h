#pragma once

#include <cstdint>
#include <memory>
#include <vector>

namespace drone {

struct Vec3i {
  int x;
  int y;
  int z;

  bool operator==(const Vec3i &other) const {
    return x == other.x && y == other.y && z == other.z;
  }
};

struct GridBounds {
  int width;
  int height;
  int depth;

  [[nodiscard]] bool Contains(const Vec3i &p) const;
  [[nodiscard]] int CellCount() const;
};

enum class CellState : std::int32_t {
  kUnknown = 0,
  kFree = 1,
  kBlocked = 2,
  kTarget = 3,
};

struct CellStateUpdate {
  Vec3i position;
  CellState state;
};

struct PathRequest {
  GridBounds bounds;
  Vec3i start;
  Vec3i goal;

  // Cells default to unknown. Only cells marked free or target are traversable.
  // Unknown and blocked cells are not treated as safe for route planning.
  std::vector<CellStateUpdate> discovered_cells;
};

struct PathResult {
  bool success = false;
  std::vector<Vec3i> points;
};

class IPathPlanner {
public:
  virtual ~IPathPlanner() = default;
  [[nodiscard]] virtual PathResult Plan(const PathRequest &request) const = 0;
};

enum class PlannerType : std::int32_t {
  kAStar = 0,
};

std::unique_ptr<IPathPlanner> CreatePathPlanner(PlannerType type);
PathResult PlanPath(const IPathPlanner &planner, const PathRequest &request);

} // namespace drone

extern "C" {

struct DroneVec3i {
  int x;
  int y;
  int z;
};

enum DronePlannerType : std::int32_t {
  DRONE_PLANNER_ASTAR = 0,
};

enum DroneCellState : std::int32_t {
  DRONE_CELL_UNKNOWN = 0,
  DRONE_CELL_FREE = 1,
  DRONE_CELL_BLOCKED = 2,
  DRONE_CELL_TARGET = 3,
};

/**
 * Unity entry point.
 *
 * Cells not listed in known_cells default to DRONE_CELL_UNKNOWN and are not
 * traversable. Only DRONE_CELL_FREE and DRONE_CELL_TARGET are safe to traverse.
 */
std::int32_t DronePlanKnownPath(std::int32_t planner_type, std::int32_t width,
                                std::int32_t height, std::int32_t depth,
                                DroneVec3i start, DroneVec3i goal,
                                const DroneVec3i *known_cells,
                                const std::int32_t *known_states,
                                std::int32_t known_count, DroneVec3i *out_path,
                                std::int32_t out_capacity);

} // extern "C"
