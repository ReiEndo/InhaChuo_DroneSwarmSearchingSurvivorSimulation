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

frontier::FrontierRankingRequest
MakeRankingRequest(drone::GridBounds bounds, drone::Vec3i start,
                   std::vector<drone::CellStateUpdate> cells) {
  frontier::FrontierRankingRequest request;
  request.bounds = bounds;
  request.start = start;
  request.discovered_cells = std::move(cells);
  request.settings.travel_cost_weight = 1.0F;
  request.settings.information_gain_weight = 0.0F;
  request.settings.information_gain_radius = 1;
  request.settings.recent_goal_penalty = 10.0F;
  request.settings.same_goal_penalty = 5.0F;
  request.settings.nearby_drone_penalty_radius = 1.5F;
  return request;
}

DroneFrontierScoringSettings DefaultAbiSettings() {
  return {1.0F, 0.0F, 1, 10.0F, 5.0F, 1.5F};
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

TEST(FrontierRankingTest, RanksFreeFrontierCandidatesByScore) {
  const auto candidates = frontier::RankFrontierCandidates(
      MakeRankingRequest({4, 3, 1}, {0, 1, 0},
                         {{{0, 1, 0}, drone::CellState::kFree},
                          {{1, 1, 0}, drone::CellState::kFree},
                          {{3, 1, 0}, drone::CellState::kFree}}));

  ASSERT_EQ(candidates.size(), 2);
  EXPECT_EQ(candidates[0].cell, (drone::Vec3i{1, 1, 0}));
  EXPECT_EQ(candidates[1].cell, (drone::Vec3i{3, 1, 0}));
}

TEST(FrontierRankingTest, TreatsTargetCellsAsTraversableFrontierCandidates) {
  const auto candidates = frontier::RankFrontierCandidates(
      MakeRankingRequest({3, 3, 1}, {0, 1, 0},
                         {{{0, 1, 0}, drone::CellState::kFree},
                          {{1, 1, 0}, drone::CellState::kTarget}}));

  ASSERT_EQ(candidates.size(), 1);
  EXPECT_EQ(candidates[0].cell, (drone::Vec3i{1, 1, 0}));
}

TEST(FrontierRankingTest, ExcludesBlockedUnknownOutOfBoundsAndStartCells) {
  const auto candidates = frontier::RankFrontierCandidates(
      MakeRankingRequest({3, 3, 1}, {1, 1, 0},
                         {{{1, 1, 0}, drone::CellState::kFree},
                          {{0, 1, 0}, drone::CellState::kBlocked},
                          {{2, 1, 0}, drone::CellState::kUnknown},
                          {{4, 1, 0}, drone::CellState::kFree}}));

  EXPECT_TRUE(candidates.empty());
}

TEST(FrontierRankingTest, InformationGainCanImproveCandidateRank) {
  auto request = MakeRankingRequest({5, 3, 1}, {2, 1, 0},
                                    {{{2, 1, 0}, drone::CellState::kFree},
                                     {{1, 1, 0}, drone::CellState::kFree},
                                     {{3, 1, 0}, drone::CellState::kFree},
                                     {{0, 0, 0}, drone::CellState::kFree},
                                     {{0, 1, 0}, drone::CellState::kFree},
                                     {{0, 2, 0}, drone::CellState::kFree}});
  request.settings.travel_cost_weight = 0.0F;
  request.settings.information_gain_weight = 1.0F;

  const auto candidates = frontier::RankFrontierCandidates(request);

  ASSERT_GE(candidates.size(), 2);
  EXPECT_EQ(candidates[0].cell, (drone::Vec3i{3, 1, 0}));
}

TEST(FrontierRankingTest, RecentGoalPenaltyWorsensOnlyMatchingCandidate) {
  auto request = MakeRankingRequest({4, 3, 1}, {0, 1, 0},
                                    {{{0, 1, 0}, drone::CellState::kFree},
                                     {{1, 1, 0}, drone::CellState::kFree},
                                     {{2, 1, 0}, drone::CellState::kFree}});
  request.settings.travel_cost_weight = 0.0F;
  request.settings.information_gain_weight = 0.0F;
  request.recent_goal_cells.push_back({1, 1, 0});

  const auto candidates = frontier::RankFrontierCandidates(request);

  ASSERT_EQ(candidates.size(), 2);
  EXPECT_EQ(candidates[0].cell, (drone::Vec3i{2, 1, 0}));
  EXPECT_EQ(candidates[1].cell, (drone::Vec3i{1, 1, 0}));
}

TEST(FrontierRankingTest, OccupiedGoalPenaltyAppliesForEveryGoalWithinRadius) {
  auto request = MakeRankingRequest({5, 3, 1}, {0, 1, 0},
                                    {{{0, 1, 0}, drone::CellState::kFree},
                                     {{1, 1, 0}, drone::CellState::kFree},
                                     {{2, 1, 0}, drone::CellState::kFree}});
  request.settings.travel_cost_weight = 0.0F;
  request.settings.information_gain_weight = 0.0F;
  request.occupied_goal_cells.push_back({1, 1, 0});
  request.occupied_goal_cells.push_back({1, 2, 0});

  const auto candidates = frontier::RankFrontierCandidates(request);

  ASSERT_EQ(candidates.size(), 2);
  EXPECT_EQ(candidates[0].cell, (drone::Vec3i{2, 1, 0}));
  EXPECT_FLOAT_EQ(candidates[1].score, 10.0F);
}

TEST(FrontierRankingTest, DeterministicTieBreaksProduceStableOrdering) {
  auto request = MakeRankingRequest({3, 3, 3}, {1, 1, 1},
                                    {{{1, 1, 1}, drone::CellState::kFree},
                                     {{0, 1, 1}, drone::CellState::kFree},
                                     {{2, 1, 1}, drone::CellState::kFree}});
  request.settings.travel_cost_weight = 1.0F;
  request.settings.information_gain_weight = 0.0F;

  const auto candidates = frontier::RankFrontierCandidates(request);

  ASSERT_EQ(candidates.size(), 2);
  EXPECT_EQ(candidates[0].cell, (drone::Vec3i{0, 1, 1}));
  EXPECT_EQ(candidates[1].cell, (drone::Vec3i{2, 1, 1}));
}

TEST(FrontierRankingAbiTest, ReturnsRequiredCountForNullOrSmallBuffer) {
  DroneVec3i known_cells[] = {{0, 1, 0}, {1, 1, 0}, {2, 1, 0}};
  std::int32_t known_states[] = {DRONE_CELL_FREE, DRONE_CELL_FREE,
                                 DRONE_CELL_FREE};
  DroneFrontierCandidate one_candidate{};

  const std::int32_t required = DroneRankFrontierCandidates(
      4, 3, 1, {0, 1, 0}, known_cells, known_states, 3, nullptr, 0, nullptr, 0,
      DefaultAbiSettings(), nullptr, 0);
  const std::int32_t small = DroneRankFrontierCandidates(
      4, 3, 1, {0, 1, 0}, known_cells, known_states, 3, nullptr, 0, nullptr, 0,
      DefaultAbiSettings(), &one_candidate, 1);

  EXPECT_EQ(required, 2);
  EXPECT_EQ(small, 2);
}

TEST(FrontierRankingAbiTest, ReturnsZeroForInvalidInputs) {
  DroneVec3i known_cells[] = {{0, 1, 0}, {1, 1, 0}};
  std::int32_t known_states[] = {DRONE_CELL_FREE, 99};
  DroneFrontierScoringSettings bad_settings = DefaultAbiSettings();
  bad_settings.travel_cost_weight = -1.0F;

  EXPECT_EQ(DroneRankFrontierCandidates(0, 3, 1, {0, 1, 0}, known_cells,
                                        known_states, 2, nullptr, 0, nullptr, 0,
                                        DefaultAbiSettings(), nullptr, 0),
            0);
  EXPECT_EQ(DroneRankFrontierCandidates(3, 3, 1, {0, 1, 0}, known_cells,
                                        known_states, -1, nullptr, 0, nullptr,
                                        0, DefaultAbiSettings(), nullptr, 0),
            0);
  EXPECT_EQ(DroneRankFrontierCandidates(3, 3, 1, {0, 1, 0}, nullptr,
                                        known_states, 2, nullptr, 0, nullptr, 0,
                                        DefaultAbiSettings(), nullptr, 0),
            0);
  EXPECT_EQ(DroneRankFrontierCandidates(3, 3, 1, {0, 1, 0}, known_cells,
                                        known_states, 2, nullptr, 1, nullptr, 0,
                                        DefaultAbiSettings(), nullptr, 0),
            0);
  EXPECT_EQ(DroneRankFrontierCandidates(3, 3, 1, {0, 1, 0}, known_cells,
                                        known_states, 2, nullptr, 0, nullptr, 0,
                                        bad_settings, nullptr, 0),
            0);
}
