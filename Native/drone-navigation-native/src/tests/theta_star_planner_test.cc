#include "../algorithms/theta_star/planner.h"
#include "../core/path.h"

#include "gtest/gtest.h"

#include <cstddef>
#include <utility>
#include <vector>

namespace theta_star = drone::algorithms::theta_star;

namespace {

drone::PathRequest MakeKnownRequest(drone::GridBounds bounds,
                                    drone::Vec3i start, drone::Vec3i goal,
                                    std::vector<drone::CellStateUpdate> cells) {
  return {bounds, start, goal, std::move(cells)};
}

std::vector<drone::CellStateUpdate> FullFreeGrid(drone::GridBounds bounds,
                                                 drone::Vec3i target) {
  std::vector<drone::CellStateUpdate> cells;
  cells.reserve(static_cast<std::size_t>(bounds.CellCount()));
  for (int z = 0; z < bounds.depth; ++z) {
    for (int y = 0; y < bounds.height; ++y) {
      for (int x = 0; x < bounds.width; ++x) {
        const drone::Vec3i position{x, y, z};
        cells.push_back({position, position == target
                                       ? drone::CellState::kTarget
                                       : drone::CellState::kFree});
      }
    }
  }
  return cells;
}

} // namespace

TEST(ThetaStarPlannerTest, FindsAnyAngleShortcutThroughKnownSafeCells) {
  const auto result = theta_star::PlanPath(MakeKnownRequest(
      {3, 3, 1}, {0, 0, 0}, {2, 2, 0}, FullFreeGrid({3, 3, 1}, {2, 2, 0})));

  ASSERT_TRUE(result.success);
  ASSERT_EQ(result.points.size(), 2);
  EXPECT_EQ(result.points.front(), (drone::Vec3i{0, 0, 0}));
  EXPECT_EQ(result.points.back(), (drone::Vec3i{2, 2, 0}));
}

TEST(ThetaStarPlannerTest, DoesNotShortcutThroughUnknownCells) {
  const auto result = theta_star::PlanPath(
      MakeKnownRequest({3, 3, 1}, {0, 0, 0}, {2, 2, 0},
                       {{{0, 0, 0}, drone::CellState::kFree},
                        {{1, 0, 0}, drone::CellState::kFree},
                        {{2, 0, 0}, drone::CellState::kFree},
                        {{2, 1, 0}, drone::CellState::kFree},
                        {{2, 2, 0}, drone::CellState::kTarget}}));

  ASSERT_TRUE(result.success);
  EXPECT_GT(result.points.size(), 2);
  EXPECT_EQ(result.points.front(), (drone::Vec3i{0, 0, 0}));
  EXPECT_EQ(result.points.back(), (drone::Vec3i{2, 2, 0}));
}

TEST(ThetaStarPlannerTest, DoesNotCutDiagonalCornerThroughBlockedCells) {
  const auto result = theta_star::PlanPath(
      MakeKnownRequest({2, 2, 1}, {0, 0, 0}, {1, 1, 0},
                       {{{0, 0, 0}, drone::CellState::kFree},
                        {{1, 0, 0}, drone::CellState::kBlocked},
                        {{0, 1, 0}, drone::CellState::kBlocked},
                        {{1, 1, 0}, drone::CellState::kTarget}}));

  EXPECT_FALSE(result.success);
  EXPECT_TRUE(result.points.empty());
}

TEST(ThetaStarPlannerTest, ReportsNoPathThroughUnknownCells) {
  const auto result = theta_star::PlanPath(
      MakeKnownRequest({3, 1, 1}, {0, 0, 0}, {2, 0, 0},
                       {{{0, 0, 0}, drone::CellState::kFree},
                        {{2, 0, 0}, drone::CellState::kTarget}}));

  EXPECT_FALSE(result.success);
  EXPECT_TRUE(result.points.empty());
}

TEST(PathPlannerTest, CreatesThetaStarPlannerByType) {
  auto planner = drone::CreatePathPlanner(drone::PlannerType::kThetaStar);
  ASSERT_NE(planner, nullptr);

  const auto result = drone::PlanPath(
      *planner, MakeKnownRequest({3, 3, 1}, {0, 0, 0}, {2, 2, 0},
                                 FullFreeGrid({3, 3, 1}, {2, 2, 0})));

  ASSERT_TRUE(result.success);
  EXPECT_EQ(result.points.size(), 2);
}

TEST(PathPlannerCTest, PlansWithThetaStar) {
  const DroneVec3i known_cells[] = {{0, 0, 0}, {1, 0, 0}, {2, 0, 0},
                                    {0, 1, 0}, {1, 1, 0}, {2, 1, 0},
                                    {0, 2, 0}, {1, 2, 0}, {2, 2, 0}};
  const std::int32_t known_states[] = {
      DRONE_CELL_FREE, DRONE_CELL_FREE, DRONE_CELL_FREE,
      DRONE_CELL_FREE, DRONE_CELL_FREE, DRONE_CELL_FREE,
      DRONE_CELL_FREE, DRONE_CELL_FREE, DRONE_CELL_TARGET};
  DroneVec3i out[2]{};

  EXPECT_EQ(DronePlanKnownPath(DRONE_PLANNER_THETA_STAR, 3, 3, 1, {0, 0, 0},
                               {2, 2, 0}, known_cells, known_states, 9, out, 2),
            2);
  EXPECT_EQ(out[1].x, 2);
  EXPECT_EQ(out[1].y, 2);
}
