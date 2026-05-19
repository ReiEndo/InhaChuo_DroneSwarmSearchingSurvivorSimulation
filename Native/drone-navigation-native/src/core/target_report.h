#pragma once

#include "path.h"

#include <cstdint>
#include <optional>
#include <vector>

namespace drone {

struct TargetReport {
  Vec3i position;
  std::int64_t observed_at = 0;
  std::int32_t reporter_id = 0;
};

struct TargetReportPayload {
  std::vector<TargetReport> reports;
};

class TargetReportStore {
public:
  [[nodiscard]] std::optional<TargetReport> LatestReport() const;
  [[nodiscard]] TargetReportPayload
  ExtractReportsSince(std::int64_t timestamp) const;

  bool Record(TargetReport report);
  std::size_t Merge(const TargetReportPayload &payload);

private:
  std::optional<TargetReport> latest_report_;
};

} // namespace drone
