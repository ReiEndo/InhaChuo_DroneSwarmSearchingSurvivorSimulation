#include "planner.h"

#include <algorithm>
#include <cmath>
#include <limits>
#include <queue>
#include <vector>

namespace drone::algorithms::theta_star {
namespace {

struct Node {
  Vec3i position;
  double estimated_total_cost;
  double cost_from_start;
};

struct NodeGreater {
  bool operator()(const Node &a, const Node &b) const {
    if (a.estimated_total_cost != b.estimated_total_cost)
      return a.estimated_total_cost > b.estimated_total_cost;
    return a.cost_from_start < b.cost_from_start;
  }
};

int IndexOf(GridBounds bounds, Vec3i p) {
  return (p.z * bounds.height + p.y) * bounds.width + p.x;
}

Vec3i PointAt(GridBounds bounds, int index) {
  return {index % bounds.width, (index / bounds.width) % bounds.height,
          index / (bounds.width * bounds.height)};
}

bool IsTraversable(CellState state) {
  return state == CellState::kFree || state == CellState::kTarget;
}

double Euclidean(Vec3i a, Vec3i b) {
  const double dx = static_cast<double>(a.x - b.x);
  const double dy = static_cast<double>(a.y - b.y);
  const double dz = static_cast<double>(a.z - b.z);
  return std::sqrt(dx * dx + dy * dy + dz * dz);
}

double Lerp(double a, double b, double t) { return a + (b - a) * t; }

bool IsSafe(GridBounds bounds, const std::vector<CellState> &cells, Vec3i p) {
  return bounds.Contains(p) && IsTraversable(cells[IndexOf(bounds, p)]);
}

bool HasLineOfSight(GridBounds bounds, const std::vector<CellState> &cells,
                    Vec3i from, Vec3i to) {
  const int dx = std::abs(to.x - from.x);
  const int dy = std::abs(to.y - from.y);
  const int dz = std::abs(to.z - from.z);
  const int steps = std::max({dx, dy, dz});
  if (steps == 0)
    return IsSafe(bounds, cells, from);

  Vec3i previous = from;
  if (!IsSafe(bounds, cells, previous))
    return false;

  for (int i = 1; i <= steps; ++i) {
    const double t = static_cast<double>(i) / static_cast<double>(steps);
    Vec3i current{static_cast<int>(std::round(Lerp(from.x, to.x, t))),
                  static_cast<int>(std::round(Lerp(from.y, to.y, t))),
                  static_cast<int>(std::round(Lerp(from.z, to.z, t)))};
    if (!IsSafe(bounds, cells, current))
      return false;

    Vec3i delta{current.x - previous.x, current.y - previous.y,
                current.z - previous.z};
    for (int x = 0; x <= std::abs(delta.x); ++x) {
      for (int y = 0; y <= std::abs(delta.y); ++y) {
        for (int z = 0; z <= std::abs(delta.z); ++z) {
          Vec3i bridged{previous.x + (delta.x < 0 ? -x : x),
                        previous.y + (delta.y < 0 ? -y : y),
                        previous.z + (delta.z < 0 ? -z : z)};
          if (!IsSafe(bounds, cells, bridged))
            return false;
        }
      }
    }

    previous = current;
  }

  return true;
}

std::vector<Vec3i> ReconstructPath(GridBounds bounds, Vec3i start, Vec3i goal,
                                   const std::vector<int> &came_from) {
  std::vector<Vec3i> reversed;
  Vec3i current = goal;
  reversed.push_back(current);

  while (!(current == start)) {
    const int parent = came_from[IndexOf(bounds, current)];
    if (parent < 0)
      return {};
    current = PointAt(bounds, parent);
    reversed.push_back(current);
  }

  std::reverse(reversed.begin(), reversed.end());
  return reversed;
}

} // namespace

PathResult Planner::Plan(const PathRequest &request) const {
  const GridBounds bounds = request.bounds;
  const Vec3i start = request.start;
  const Vec3i goal = request.goal;

  const int cell_count = bounds.CellCount();
  if (cell_count == 0 || !bounds.Contains(start) || !bounds.Contains(goal)) {
    return {};
  }

  std::vector<CellState> cells(cell_count, CellState::kUnknown);
  for (const CellStateUpdate &cell : request.discovered_cells) {
    if (bounds.Contains(cell.position)) {
      cells[IndexOf(bounds, cell.position)] = cell.state;
    }
  }

  const int start_index = IndexOf(bounds, start);
  const int goal_index = IndexOf(bounds, goal);
  if (!IsTraversable(cells[start_index]) || !IsTraversable(cells[goal_index])) {
    return {};
  }

  std::vector<double> g_score(cell_count, std::numeric_limits<double>::max());
  std::vector<int> came_from(cell_count, -1);
  std::priority_queue<Node, std::vector<Node>, NodeGreater> open;

  g_score[start_index] = 0.0;
  came_from[start_index] = start_index;
  open.push(Node{start, Euclidean(start, goal), 0.0});

  while (!open.empty()) {
    const Node current = open.top();
    open.pop();

    const int current_index = IndexOf(bounds, current.position);
    if (current.cost_from_start != g_score[current_index])
      continue;
    if (current_index == goal_index) {
      return PathResult{true, ReconstructPath(bounds, start, goal, came_from)};
    }

    for (int dz = -1; dz <= 1; ++dz) {
      for (int dy = -1; dy <= 1; ++dy) {
        for (int dx = -1; dx <= 1; ++dx) {
          if (dx == 0 && dy == 0 && dz == 0)
            continue;

          Vec3i next{current.position.x + dx, current.position.y + dy,
                     current.position.z + dz};
          if (!bounds.Contains(next))
            continue;

          const int next_index = IndexOf(bounds, next);
          if (!IsTraversable(cells[next_index]))
            continue;

          Vec3i parent = PointAt(bounds, came_from[current_index]);
          int candidate_parent = current_index;
          double tentative_g =
              g_score[current_index] + Euclidean(current.position, next);

          if (!HasLineOfSight(bounds, cells, current.position, next))
            continue;

          if (HasLineOfSight(bounds, cells, parent, next)) {
            const int parent_index = IndexOf(bounds, parent);
            const double parent_g =
                g_score[parent_index] + Euclidean(parent, next);
            if (parent_g <= tentative_g) {
              candidate_parent = parent_index;
              tentative_g = parent_g;
            }
          }

          if (tentative_g >= g_score[next_index])
            continue;

          came_from[next_index] = candidate_parent;
          g_score[next_index] = tentative_g;
          open.push(
              Node{next, tentative_g + Euclidean(next, goal), tentative_g});
        }
      }
    }
  }

  return {};
}

PathResult PlanPath(const PathRequest &request) {
  return Planner{}.Plan(request);
}

} // namespace drone::algorithms::theta_star
