#include "../core/target_report.h"

#include "gtest/gtest.h"

namespace {

TEST(TargetReportStoreTest, RecordsLatestObservedTarget) {
  drone::TargetReportStore store;

  EXPECT_TRUE(store.Record({{1, 2, 3}, 10, 7}));

  const auto latest = store.LatestReport();
  ASSERT_TRUE(latest.has_value());
  EXPECT_EQ(latest->position, (drone::Vec3i{1, 2, 3}));
  EXPECT_EQ(latest->observed_at, 10);
  EXPECT_EQ(latest->reporter_id, 7);
}

TEST(TargetReportStoreTest, RejectsStaleReports) {
  drone::TargetReportStore store;

  EXPECT_TRUE(store.Record({{1, 0, 0}, 20, 1}));
  EXPECT_FALSE(store.Record({{2, 0, 0}, 19, 2}));
  EXPECT_FALSE(store.Record({{3, 0, 0}, 20, 3}));

  const auto latest = store.LatestReport();
  ASSERT_TRUE(latest.has_value());
  EXPECT_EQ(latest->position, (drone::Vec3i{1, 0, 0}));
  EXPECT_EQ(latest->reporter_id, 1);
}

TEST(TargetReportStoreTest, MergePropagatesNewestReportAcrossStores) {
  drone::TargetReportStore scout;
  drone::TargetReportStore relay;
  drone::TargetReportStore command;

  ASSERT_TRUE(scout.Record({{4, 5, 6}, 30, 42}));

  EXPECT_EQ(relay.Merge(scout.ExtractReportsSince(0)), 1);
  EXPECT_EQ(command.Merge(relay.ExtractReportsSince(0)), 1);

  const auto command_report = command.LatestReport();
  ASSERT_TRUE(command_report.has_value());
  EXPECT_EQ(command_report->position, (drone::Vec3i{4, 5, 6}));
  EXPECT_EQ(command_report->observed_at, 30);
  EXPECT_EQ(command_report->reporter_id, 42);
}

TEST(TargetReportStoreTest, ExtractsOnlyReportsNewerThanTimestamp) {
  drone::TargetReportStore store;
  ASSERT_TRUE(store.Record({{2, 2, 2}, 50, 8}));

  EXPECT_TRUE(store.ExtractReportsSince(50).reports.empty());

  const auto payload = store.ExtractReportsSince(49);
  ASSERT_EQ(payload.reports.size(), 1);
  EXPECT_EQ(payload.reports.front().position, (drone::Vec3i{2, 2, 2}));
}

TEST(TargetReportStoreTest, MergeCountsOnlyChangedReports) {
  drone::TargetReportStore store;

  const std::size_t changed = store.Merge(
      {{{{1, 0, 0}, 10, 1}, {{2, 0, 0}, 9, 2}, {{3, 0, 0}, 11, 3}}});

  EXPECT_EQ(changed, 2);
  const auto latest = store.LatestReport();
  ASSERT_TRUE(latest.has_value());
  EXPECT_EQ(latest->position, (drone::Vec3i{3, 0, 0}));
}

} // namespace
