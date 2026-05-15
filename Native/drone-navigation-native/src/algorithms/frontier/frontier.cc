#include "frontier.h"

#include <algorithm>
#include <cstdlib>

namespace drone::algorithms::frontier {
namespace {

int IndexOf(GridBounds bounds, Vec3i p) {
  return (p.z * bounds.height + p.y) * bounds.width + p.x;
}

Vec3i PointAt(GridBounds bounds, int index) {
  return {index % bounds.width, (index / bounds.width) % bounds.height,
          index / (bounds.width * bounds.height)};
}

int Manhattan(Vec3i a, Vec3i b) {
  return std::abs(a.x - b.x) + std::abs(a.y - b.y) + std::abs(a.z - b.z);
}

bool IsFrontier(const std::vector<CellState> &cells, GridBounds bounds,
                Vec3i point) {
  if (cells[IndexOf(bounds, point)] != CellState::kFree)
    return false;

  constexpr Vec3i kDirs[] = {{1, 0, 0},  {-1, 0, 0}, {0, 1, 0},
                             {0, -1, 0}, {0, 0, 1},  {0, 0, -1}};
  for (const Vec3i &d : kDirs) {
    const Vec3i adjacent{point.x + d.x, point.y + d.y, point.z + d.z};
    if (bounds.Contains(adjacent) &&
        cells[IndexOf(bounds, adjacent)] == CellState::kUnknown) {
      return true;
    }
  }
  return false;
}

std::vector<CellState> BuildCellMap(const FrontierSearchRequest &request) {
  std::vector<CellState> cells(
      static_cast<std::size_t>(request.bounds.CellCount()),
      CellState::kUnknown);
  for (const CellStateUpdate &cell : request.discovered_cells) {
    if (request.bounds.Contains(cell.position)) {
      cells[IndexOf(request.bounds, cell.position)] = cell.state;
    }
  }
  return cells;
}

} // namespace

std::vector<Vec3i> FindFrontiers(const FrontierSearchRequest &request) {
  const GridBounds bounds = request.bounds;
  const int cell_count = bounds.CellCount();
  if (cell_count == 0 || !bounds.Contains(request.start))
    return {};

  const std::vector<CellState> cells = BuildCellMap(request);
  std::vector<Vec3i> frontiers;
  for (int i = 0; i < cell_count; ++i) {
    const Vec3i point = PointAt(bounds, i);
    if (IsFrontier(cells, bounds, point)) {
      frontiers.push_back(point);
    }
  }

  std::sort(frontiers.begin(), frontiers.end(),
            [start = request.start](Vec3i a, Vec3i b) {
              const int distance_a = Manhattan(start, a);
              const int distance_b = Manhattan(start, b);
              if (distance_a != distance_b)
                return distance_a < distance_b;
              if (a.z != b.z)
                return a.z < b.z;
              if (a.y != b.y)
                return a.y < b.y;
              return a.x < b.x;
            });
  return frontiers;
}

std::optional<ReachableFrontier>
FindNearestReachableFrontier(const IPathPlanner &planner,
                             const FrontierSearchRequest &request) {
  std::optional<ReachableFrontier> nearest;
  for (const Vec3i &frontier : FindFrontiers(request)) {
    PathRequest path_request{request.bounds, request.start, frontier,
                             request.discovered_cells};
    const PathResult path = PlanPath(planner, path_request);
    if (!path.success) {
      continue;
    }
    if (!nearest.has_value() ||
        path.points.size() < nearest->path.points.size()) {
      nearest = ReachableFrontier{frontier, path};
    }
  }
  return nearest;
}

} // namespace drone::algorithms::frontier
