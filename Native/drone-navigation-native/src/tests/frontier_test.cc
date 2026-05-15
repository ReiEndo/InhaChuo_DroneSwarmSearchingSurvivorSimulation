#include "../algorithms/astar/planner.h"
#include "../algorithms/frontier/frontier.h"

#include "gtest/gtest.h"

#include <cstddef>
#include <utility>
#include <vector>

namespace frontier = drone::algorithms::frontier;

namespace {

frontier::FrontierSearchRequest
MakeRequest(drone::GridBounds bounds, drone::Vec3i start,
            std::vector<drone::CellStateUpdate> cells) {
  return {bounds, start, std::move(cells)};
}

class FixedCostPlanner final : public drone::IPathPlanner {
public:
  [[nodiscard]] drone::PathResult
  Plan(const drone::PathRequest &request) const override {
    if (request.goal == drone::Vec3i{1, 0, 0}) {
      return MakePath(5);
    }
    if (request.goal == drone::Vec3i{3, 0, 0}) {
      return MakePath(3);
    }
    return {};
  }

private:
  static drone::PathResult MakePath(std::size_t length) {
    return {true, std::vector<drone::Vec3i>(length)};
  }
};

} // namespace

TEST(FrontierTest, DetectsKnownFreeCellAdjacentToUnknownIn3D) {
  const auto frontiers = frontier::FindFrontiers(MakeRequest(
      {3, 3, 3}, {1, 1, 1}, {{{1, 1, 1}, drone::CellState::kFree}}));

  ASSERT_EQ(frontiers.size(), 1);
  EXPECT_EQ(frontiers.front(), (drone::Vec3i{1, 1, 1}));
}

TEST(FrontierTest, DoesNotTreatOutOfBoundsAsUnknown) {
  const auto frontiers = frontier::FindFrontiers(MakeRequest(
      {1, 1, 1}, {0, 0, 0}, {{{0, 0, 0}, drone::CellState::kFree}}));

  EXPECT_TRUE(frontiers.empty());
}

TEST(FrontierTest, ReportsNoFrontiersWhenAllNeighborsAreKnown) {
  const auto frontiers = frontier::FindFrontiers(
      MakeRequest({3, 1, 1}, {1, 0, 0},
                  {{{0, 0, 0}, drone::CellState::kFree},
                   {{1, 0, 0}, drone::CellState::kFree},
                   {{2, 0, 0}, drone::CellState::kFree}}));

  EXPECT_TRUE(frontiers.empty());
}

TEST(FrontierTest, BlockedAndTargetCellsAreNotFrontiers) {
  const auto frontiers = frontier::FindFrontiers(
      MakeRequest({3, 2, 1}, {0, 0, 0},
                  {{{0, 0, 0}, drone::CellState::kFree},
                   {{1, 0, 0}, drone::CellState::kBlocked},
                   {{2, 0, 0}, drone::CellState::kTarget}}));

  ASSERT_EQ(frontiers.size(), 1);
  EXPECT_EQ(frontiers.front(), (drone::Vec3i{0, 0, 0}));
}

TEST(FrontierTest, SelectsNearestReachableFrontier) {
  FixedCostPlanner planner;
  const auto result = frontier::FindNearestReachableFrontier(
      planner, MakeRequest({5, 2, 1}, {0, 0, 0},
                           {{{0, 0, 0}, drone::CellState::kFree},
                            {{1, 0, 0}, drone::CellState::kFree},
                            {{2, 0, 0}, drone::CellState::kBlocked},
                            {{3, 0, 0}, drone::CellState::kFree}}));

  ASSERT_TRUE(result.has_value());
  const frontier::ReachableFrontier &reachable =
      result.value(); // NOLINT(bugprone-unchecked-optional-access)
  EXPECT_EQ(reachable.target, (drone::Vec3i{3, 0, 0}));
  ASSERT_TRUE(reachable.path.success);
  EXPECT_EQ(reachable.path.points.size(), 3);
}

TEST(FrontierTest, ReportsNoReachableFrontierWhenAllCandidatesAreBlockedOff) {
  drone::algorithms::astar::Planner planner;
  const auto result = frontier::FindNearestReachableFrontier(
      planner, MakeRequest({5, 1, 1}, {0, 0, 0},
                           {{{0, 0, 0}, drone::CellState::kBlocked},
                            {{2, 0, 0}, drone::CellState::kFree},
                            {{3, 0, 0}, drone::CellState::kFree},
                            {{4, 0, 0}, drone::CellState::kFree}}));

  EXPECT_FALSE(result.has_value());
}
