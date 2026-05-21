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

float SquaredDistance(Vec3i a, Vec3i b) {
  const float dx = static_cast<float>(a.x - b.x);
  const float dy = static_cast<float>(a.y - b.y);
  const float dz = static_cast<float>(a.z - b.z);
  return dx * dx + dy * dy + dz * dz;
}

bool IsTraversableFrontierState(CellState state) {
  return state == CellState::kFree || state == CellState::kTarget;
}

bool HasUnknownNeighbor(const std::vector<CellState> &cells, GridBounds bounds,
                        Vec3i point) {
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

bool IsFrontier(const std::vector<CellState> &cells, GridBounds bounds,
                Vec3i point, bool include_targets) {
  const CellState state = cells[IndexOf(bounds, point)];
  if (state != CellState::kFree &&
      (!include_targets || !IsTraversableFrontierState(state))) {
    return false;
  }
  return HasUnknownNeighbor(cells, bounds, point);
}

std::vector<CellState> BuildCellMap(
    GridBounds bounds, const std::vector<CellStateUpdate> &discovered_cells) {
  std::vector<CellState> cells(static_cast<std::size_t>(bounds.CellCount()),
                               CellState::kUnknown);
  for (const CellStateUpdate &cell : discovered_cells) {
    if (bounds.Contains(cell.position)) {
      cells[IndexOf(bounds, cell.position)] = cell.state;
    }
  }
  return cells;
}

int CountUnknownCellsWithinRadius(const std::vector<CellState> &cells,
                                  GridBounds bounds, Vec3i center, int radius) {
  int count = 0;
  for (int z = center.z - radius; z <= center.z + radius; ++z) {
    for (int y = center.y - radius; y <= center.y + radius; ++y) {
      for (int x = center.x - radius; x <= center.x + radius; ++x) {
        const Vec3i cell{x, y, z};
        if (bounds.Contains(cell) &&
            cells[IndexOf(bounds, cell)] == CellState::kUnknown) {
          ++count;
        }
      }
    }
  }
  return count;
}

bool ContainsCell(const std::vector<Vec3i> &cells, Vec3i cell) {
  return std::find(cells.begin(), cells.end(), cell) != cells.end();
}

float OccupiedGoalPenalty(const std::vector<Vec3i> &occupied_goal_cells,
                          Vec3i candidate,
                          const FrontierScoringSettings &settings) {
  if (settings.nearby_drone_penalty_radius <= 0.0F ||
      settings.same_goal_penalty <= 0.0F) {
    return 0.0F;
  }

  const float radius_squared = settings.nearby_drone_penalty_radius *
                               settings.nearby_drone_penalty_radius;
  float penalty = 0.0F;
  for (const Vec3i &occupied_goal : occupied_goal_cells) {
    if (SquaredDistance(candidate, occupied_goal) <= radius_squared) {
      penalty += settings.same_goal_penalty;
    }
  }
  return penalty;
}

struct CandidateWithGain {
  RankedFrontierCandidate candidate;
  int information_gain = 0;
};

} // namespace

std::vector<Vec3i> FindFrontiers(const FrontierSearchRequest &request) {
  const GridBounds bounds = request.bounds;
  const int cell_count = bounds.CellCount();
  if (cell_count == 0 || !bounds.Contains(request.start))
    return {};

  const std::vector<CellState> cells =
      BuildCellMap(request.bounds, request.discovered_cells);
  std::vector<Vec3i> frontiers;
  for (int i = 0; i < cell_count; ++i) {
    const Vec3i point = PointAt(bounds, i);
    if (IsFrontier(cells, bounds, point, false)) {
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

std::vector<RankedFrontierCandidate>
RankFrontierCandidates(const FrontierRankingRequest &request) {
  const GridBounds bounds = request.bounds;
  const int cell_count = bounds.CellCount();
  if (cell_count == 0 || !bounds.Contains(request.start))
    return {};

  const std::vector<CellState> cells =
      BuildCellMap(request.bounds, request.discovered_cells);
  std::vector<CandidateWithGain> ranked;
  for (int i = 0; i < cell_count; ++i) {
    const Vec3i point = PointAt(bounds, i);
    if (point == request.start || !IsFrontier(cells, bounds, point, true)) {
      continue;
    }

    const float distance_squared = SquaredDistance(request.start, point);
    const int information_gain = CountUnknownCellsWithinRadius(
        cells, bounds, point, request.settings.information_gain_radius);
    float score = request.settings.travel_cost_weight * distance_squared -
                  request.settings.information_gain_weight *
                      static_cast<float>(information_gain);
    if (ContainsCell(request.recent_goal_cells, point)) {
      score += request.settings.recent_goal_penalty;
    }
    score += OccupiedGoalPenalty(request.occupied_goal_cells, point,
                                 request.settings);

    ranked.push_back({{point, score, distance_squared}, information_gain});
  }

  std::sort(ranked.begin(), ranked.end(),
            [](const CandidateWithGain &left, const CandidateWithGain &right) {
              if (left.candidate.score != right.candidate.score)
                return left.candidate.score < right.candidate.score;
              if (left.information_gain != right.information_gain)
                return left.information_gain > right.information_gain;
              if (left.candidate.raw_distance_squared !=
                  right.candidate.raw_distance_squared) {
                return left.candidate.raw_distance_squared <
                       right.candidate.raw_distance_squared;
              }
              if (left.candidate.cell.z != right.candidate.cell.z)
                return left.candidate.cell.z < right.candidate.cell.z;
              if (left.candidate.cell.y != right.candidate.cell.y)
                return left.candidate.cell.y < right.candidate.cell.y;
              return left.candidate.cell.x < right.candidate.cell.x;
            });

  std::vector<RankedFrontierCandidate> candidates;
  candidates.reserve(ranked.size());
  for (const CandidateWithGain &candidate : ranked) {
    candidates.push_back(candidate.candidate);
  }
  return candidates;
}

} // namespace drone::algorithms::frontier

