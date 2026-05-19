#include "target_report.h"

namespace drone {

std::optional<TargetReport> TargetReportStore::LatestReport() const {
  return latest_report_;
}

TargetReportPayload
TargetReportStore::ExtractReportsSince(std::int64_t timestamp) const {
  TargetReportPayload payload;
  if (latest_report_.has_value() && latest_report_->observed_at > timestamp) {
    payload.reports.push_back(*latest_report_);
  }
  return payload;
}

bool TargetReportStore::Record(TargetReport report) {
  if (latest_report_.has_value() &&
      report.observed_at <= latest_report_->observed_at) {
    return false;
  }

  latest_report_ = report;
  return true;
}

std::size_t TargetReportStore::Merge(const TargetReportPayload &payload) {
  std::size_t changed = 0;
  for (const TargetReport &report : payload.reports) {
    if (Record(report)) {
      ++changed;
    }
  }
  return changed;
}

} // namespace drone
