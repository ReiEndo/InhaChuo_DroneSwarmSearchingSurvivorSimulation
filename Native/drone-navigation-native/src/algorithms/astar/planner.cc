#include "planner.h"

#include <algorithm>
#include <cmath>
#include <limits>
#include <queue>
#include <vector>

namespace drone::algorithms::astar {
namespace {

struct Node {
  Vec3i position;
  int estimated_total_cost;
  int cost_from_start;
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

int Manhattan(Vec3i a, Vec3i b) {
  return std::abs(a.x - b.x) + std::abs(a.y - b.y) + std::abs(a.z - b.z);
}

bool IsTraversable(CellState state) {
  return state == CellState::kFree || state == CellState::kTarget;
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

  std::vector<int> g_score(cell_count, std::numeric_limits<int>::max());
  std::vector<int> came_from(cell_count, -1);
  std::priority_queue<Node, std::vector<Node>, NodeGreater> open;

  g_score[start_index] = 0;
  open.push(Node{start, Manhattan(start, goal), 0});

  constexpr Vec3i kDirs[] = {{1, 0, 0},  {-1, 0, 0}, {0, 1, 0},
                             {0, -1, 0}, {0, 0, 1},  {0, 0, -1}};

  while (!open.empty()) {
    const Node current = open.top();
    open.pop();

    const int current_index = IndexOf(bounds, current.position);
    if (current.cost_from_start != g_score[current_index])
      continue;
    if (current_index == goal_index) {
      return PathResult{true, ReconstructPath(bounds, start, goal, came_from)};
    }

    for (const Vec3i &d : kDirs) {
      Vec3i next{current.position.x + d.x, current.position.y + d.y,
                 current.position.z + d.z};
      if (!bounds.Contains(next))
        continue;
      const int next_index = IndexOf(bounds, next);
      if (!IsTraversable(cells[next_index]))
        continue;

      const int tentative_g = current.cost_from_start + 1;
      if (tentative_g >= g_score[next_index])
        continue;

      came_from[next_index] = current_index;
      g_score[next_index] = tentative_g;
      open.push(Node{next, tentative_g + Manhattan(next, goal), tentative_g});
    }
  }

  return {};
}

PathResult PlanPath(const PathRequest &request) {
  return Planner{}.Plan(request);
}

} // namespace drone::algorithms::astar
