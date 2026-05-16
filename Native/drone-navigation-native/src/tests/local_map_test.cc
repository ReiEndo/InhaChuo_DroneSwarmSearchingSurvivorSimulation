#include "../core/local_map.h"

#include "gtest/gtest.h"

#include <algorithm>

namespace {

drone::CellState StateAt(const drone::LocalMap &map, drone::Vec3i position) {
  const auto cell = map.GetCell(position);
  EXPECT_TRUE(cell.has_value());
  return cell->state;
}

} // namespace

TEST(LocalMapTest, MergesNewerFreeBlockedAndTargetStates) {
  drone::LocalMap map({3, 1, 1});

  const std::size_t changed =
      map.Merge({{{{0, 0, 0}, drone::CellState::kFree, 10},
                  {{1, 0, 0}, drone::CellState::kBlocked, 20},
                  {{2, 0, 0}, drone::CellState::kTarget, 30}}});

  EXPECT_EQ(changed, 3);
  EXPECT_EQ(StateAt(map, {0, 0, 0}), drone::CellState::kFree);
  EXPECT_EQ(StateAt(map, {1, 0, 0}), drone::CellState::kBlocked);
  EXPECT_EQ(StateAt(map, {2, 0, 0}), drone::CellState::kTarget);
}

TEST(LocalMapTest, NewerObservationReplacesOlderState) {
  drone::LocalMap map({1, 1, 1});

  EXPECT_TRUE(map.ObserveCell({0, 0, 0}, drone::CellState::kFree, 10));
  EXPECT_TRUE(map.ObserveCell({0, 0, 0}, drone::CellState::kBlocked, 11));

  const auto cell = map.GetCell({0, 0, 0});
  ASSERT_TRUE(cell.has_value());
  EXPECT_EQ(cell->state, drone::CellState::kBlocked);
  EXPECT_EQ(cell->observed_at, 11);
}

TEST(LocalMapTest, StaleUpdatesDoNotReplaceCurrentObservation) {
  drone::LocalMap map({1, 1, 1});

  EXPECT_TRUE(map.ObserveCell({0, 0, 0}, drone::CellState::kTarget, 50));
  EXPECT_FALSE(map.ObserveCell({0, 0, 0}, drone::CellState::kFree, 49));
  EXPECT_FALSE(map.ObserveCell({0, 0, 0}, drone::CellState::kBlocked, 50));

  const auto cell = map.GetCell({0, 0, 0});
  ASSERT_TRUE(cell.has_value());
  EXPECT_EQ(cell->state, drone::CellState::kTarget);
  EXPECT_EQ(cell->observed_at, 50);
}

TEST(LocalMapTest, MergeFiltersOutOfBoundsUpdates) {
  drone::LocalMap map({2, 1, 1});

  const std::size_t changed =
      map.Merge({{{{0, 0, 0}, drone::CellState::kFree, 1},
                  {{2, 0, 0}, drone::CellState::kBlocked, 2},
                  {{-1, 0, 0}, drone::CellState::kTarget, 3}}});

  EXPECT_EQ(changed, 1);
  EXPECT_TRUE(map.GetCell({0, 0, 0}).has_value());
  EXPECT_FALSE(map.GetCell({1, 0, 0}).has_value());
  EXPECT_FALSE(map.GetCell({2, 0, 0}).has_value());
}

TEST(LocalMapTest, InvalidOverflowBoundsBehaveAsEmptyMap) {
  drone::LocalMap map({50000, 50000, 50000});

  EXPECT_EQ(map.bounds().CellCount(), 0);
  EXPECT_FALSE(map.GetCell({0, 0, 0}).has_value());
  EXPECT_FALSE(map.ObserveCell({0, 0, 0}, drone::CellState::kFree, 1));
  EXPECT_EQ(map.Merge({{{{0, 0, 0}, drone::CellState::kTarget, 2}}}), 0);
  EXPECT_TRUE(map.DiscoveredCells().empty());
  EXPECT_TRUE(map.ExtractUpdatesSince(0).discovered_cells.empty());
}

TEST(LocalMapTest, ExtractsOnlyUpdatesNewerThanTimestamp) {
  drone::LocalMap map({4, 1, 1});
  EXPECT_TRUE(map.ObserveCell({0, 0, 0}, drone::CellState::kFree, 10));
  EXPECT_TRUE(map.ObserveCell({1, 0, 0}, drone::CellState::kBlocked, 20));
  EXPECT_TRUE(map.ObserveCell({2, 0, 0}, drone::CellState::kTarget, 30));
  EXPECT_TRUE(map.ObserveCell({3, 0, 0}, drone::CellState::kFree, 40));

  const auto payload = map.ExtractUpdatesSince(20);

  ASSERT_EQ(payload.discovered_cells.size(), 2);
  EXPECT_TRUE(std::all_of(payload.discovered_cells.begin(),
                          payload.discovered_cells.end(),
                          [](const drone::CellStateUpdate &update) {
                            return update.observed_at > 20;
                          }));
  EXPECT_EQ(payload.discovered_cells[0].position, (drone::Vec3i{2, 0, 0}));
  EXPECT_EQ(payload.discovered_cells[1].position, (drone::Vec3i{3, 0, 0}));
}
