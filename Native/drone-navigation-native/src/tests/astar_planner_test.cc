#include "../algorithms/astar/planner.h"
#include "../core/path.h"

#include "gtest/gtest.h"

#include <cmath>
#include <cstddef>
#include <utility>
#include <vector>

namespace astar = drone::algorithms::astar;

namespace {

drone::PathRequest MakeKnownRequest(drone::GridBounds bounds,
                                    drone::Vec3i start, drone::Vec3i goal,
                                    std::vector<drone::CellStateUpdate> cells) {
  return {bounds, start, goal, std::move(cells)};
}

void ExpectSixConnectedPath(const std::vector<drone::Vec3i> &points) {
  for (std::size_t i = 1; i < points.size(); ++i) {
    const int dx = std::abs(points[i].x - points[i - 1].x);
    const int dy = std::abs(points[i].y - points[i - 1].y);
    const int dz = std::abs(points[i].z - points[i - 1].z);
    EXPECT_EQ(dx + dy + dz, 1) << "invalid step at index " << i;
  }
}

} // namespace

TEST(AStarPlannerTest, FindsStraightPath) {
  const auto result = astar::PlanPath(
      MakeKnownRequest({5, 1, 1}, {0, 0, 0}, {4, 0, 0},
                       {{{0, 0, 0}, drone::CellState::kFree},
                        {{1, 0, 0}, drone::CellState::kFree},
                        {{2, 0, 0}, drone::CellState::kFree},
                        {{3, 0, 0}, drone::CellState::kFree},
                        {{4, 0, 0}, drone::CellState::kTarget}}));

  ASSERT_TRUE(result.success);
  ASSERT_EQ(result.points.size(), 5);
  EXPECT_EQ(result.points.front(), (drone::Vec3i{0, 0, 0}));
  EXPECT_EQ(result.points.back(), (drone::Vec3i{4, 0, 0}));
  ExpectSixConnectedPath(result.points);
}

TEST(AStarPlannerTest, FindsShortestPathAcross3DGrid) {
  const auto result = astar::PlanPath(
      MakeKnownRequest({3, 3, 3}, {0, 0, 0}, {2, 2, 2},
                       {{{0, 0, 0}, drone::CellState::kFree},
                        {{1, 0, 0}, drone::CellState::kFree},
                        {{2, 0, 0}, drone::CellState::kFree},
                        {{2, 1, 0}, drone::CellState::kFree},
                        {{2, 2, 0}, drone::CellState::kFree},
                        {{2, 2, 1}, drone::CellState::kFree},
                        {{2, 2, 2}, drone::CellState::kTarget}}));

  ASSERT_TRUE(result.success);
  ASSERT_EQ(result.points.size(), 7);
  EXPECT_EQ(result.points.front(), (drone::Vec3i{0, 0, 0}));
  EXPECT_EQ(result.points.back(), (drone::Vec3i{2, 2, 2}));
  ExpectSixConnectedPath(result.points);
}

TEST(AStarPlannerTest, ReportsNoPathThroughUnknownCells) {
  const auto result = astar::PlanPath(
      MakeKnownRequest({3, 1, 1}, {0, 0, 0}, {2, 0, 0},
                       {{{0, 0, 0}, drone::CellState::kFree},
                        {{2, 0, 0}, drone::CellState::kTarget}}));

  EXPECT_FALSE(result.success);
  EXPECT_TRUE(result.points.empty());
}

TEST(AStarPlannerTest, TraversesOnlyDiscoveredSafeCells) {
  const auto result = astar::PlanPath(
      MakeKnownRequest({3, 1, 1}, {0, 0, 0}, {2, 0, 0},
                       {{{0, 0, 0}, drone::CellState::kFree},
                        {{1, 0, 0}, drone::CellState::kFree},
                        {{2, 0, 0}, drone::CellState::kTarget}}));

  ASSERT_TRUE(result.success);
  ASSERT_EQ(result.points.size(), 3);
  EXPECT_EQ(result.points.back(), (drone::Vec3i{2, 0, 0}));
}

TEST(AStarPlannerTest, SupportsEmptyKnownMap) {
  const auto result = astar::PlanPath({{3, 1, 1}, {0, 0, 0}, {2, 0, 0}});

  EXPECT_FALSE(result.success);
  EXPECT_TRUE(result.points.empty());
}

TEST(AStarPlannerTest, DoesNotStartOrEndOnBlockedOrUnknownCells) {
  drone::PathRequest blocked_goal{{2, 1, 1}, {0, 0, 0}, {1, 0, 0}};
  blocked_goal.discovered_cells = {
      {{0, 0, 0}, drone::CellState::kFree},
      {{1, 0, 0}, drone::CellState::kBlocked},
  };

  EXPECT_FALSE(astar::PlanPath(blocked_goal).success);

  drone::PathRequest unknown_start{{2, 1, 1}, {0, 0, 0}, {1, 0, 0}};
  unknown_start.discovered_cells = {
      {{1, 0, 0}, drone::CellState::kTarget},
  };

  EXPECT_FALSE(astar::PlanPath(unknown_start).success);
}

TEST(PathPlannerTest, CreatesPlannerByType) {
  auto planner = drone::CreatePathPlanner(drone::PlannerType::kAStar);
  ASSERT_NE(planner, nullptr);

  const auto result = drone::PlanPath(
      *planner, MakeKnownRequest({5, 1, 1}, {0, 0, 0}, {4, 0, 0},
                                 {{{0, 0, 0}, drone::CellState::kFree},
                                  {{1, 0, 0}, drone::CellState::kFree},
                                  {{2, 0, 0}, drone::CellState::kFree},
                                  {{3, 0, 0}, drone::CellState::kFree},
                                  {{4, 0, 0}, drone::CellState::kTarget}}));

  ASSERT_TRUE(result.success);
  EXPECT_EQ(result.points.size(), 5);
}

TEST(PathPlannerCTest, PlansThroughKnownSafeCellsOnly) {
  const DroneVec3i known_cells[] = {{0, 0, 0}, {1, 0, 0}, {2, 0, 0}};
  const std::int32_t known_states[] = {DRONE_CELL_FREE, DRONE_CELL_FREE,
                                       DRONE_CELL_TARGET};
  DroneVec3i out[3]{};

  EXPECT_EQ(DronePlanKnownPath(DRONE_PLANNER_ASTAR, 3, 1, 1, {0, 0, 0},
                               {2, 0, 0}, known_cells, known_states, 3, out, 3),
            3);
  EXPECT_EQ(out[2].x, 2);
}

TEST(PathPlannerCTest, RejectsUnknownCellsOnKnownPath) {
  const DroneVec3i known_cells[] = {{0, 0, 0}, {2, 0, 0}};
  const std::int32_t known_states[] = {DRONE_CELL_FREE, DRONE_CELL_TARGET};

  EXPECT_EQ(DronePlanKnownPath(DRONE_PLANNER_ASTAR, 3, 1, 1, {0, 0, 0},
                               {2, 0, 0}, known_cells, known_states, 2, nullptr,
                               0),
            0);
}

TEST(PathPlannerCTest, RejectsEmptyKnownMap) {
  EXPECT_EQ(DronePlanKnownPath(DRONE_PLANNER_ASTAR, 3, 1, 1, {0, 0, 0},
                               {2, 0, 0}, nullptr, nullptr, 0, nullptr, 0),
            0);
}

TEST(PathPlannerCTest, RejectsInvalidKnownCellState) {
  const DroneVec3i known_cells[] = {{0, 0, 0}, {1, 0, 0}, {2, 0, 0}};
  const std::int32_t known_states[] = {DRONE_CELL_FREE, 99, DRONE_CELL_TARGET};

  EXPECT_EQ(DronePlanKnownPath(DRONE_PLANNER_ASTAR, 3, 1, 1, {0, 0, 0},
                               {2, 0, 0}, known_cells, known_states, 3, nullptr,
                               0),
            0);
}
