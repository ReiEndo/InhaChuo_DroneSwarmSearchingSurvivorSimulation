#include "local_map.h"

namespace drone {
namespace {

GridBounds NormalizeBounds(GridBounds bounds) {
  if (bounds.CellCount() == 0)
    return {};
  return bounds;
}

} // namespace

LocalMap::LocalMap(GridBounds bounds)
    : bounds_(NormalizeBounds(bounds)),
      cells_(static_cast<std::size_t>(bounds_.CellCount())) {}

GridBounds LocalMap::bounds() const { return bounds_; }

std::optional<CellStateUpdate> LocalMap::GetCell(Vec3i position) const {
  if (cells_.empty() || !bounds_.Contains(position))
    return std::nullopt;
  return cells_[static_cast<std::size_t>(IndexOf(position))];
}

std::vector<CellStateUpdate> LocalMap::DiscoveredCells() const {
  std::vector<CellStateUpdate> discovered;
  for (const auto &cell : cells_) {
    if (cell.has_value()) {
      discovered.push_back(*cell);
    }
  }
  return discovered;
}

MapUpdatePayload LocalMap::ExtractUpdatesSince(std::int64_t timestamp) const {
  MapUpdatePayload payload;
  for (const auto &cell : cells_) {
    if (cell.has_value() && cell->observed_at > timestamp) {
      payload.discovered_cells.push_back(*cell);
    }
  }
  return payload;
}

bool LocalMap::ObserveCell(Vec3i position, CellState state,
                           std::int64_t observed_at) {
  if (cells_.empty() || !bounds_.Contains(position))
    return false;

  auto &cell = cells_[static_cast<std::size_t>(IndexOf(position))];
  if (cell.has_value() && observed_at <= cell->observed_at)
    return false;

  cell = CellStateUpdate{position, state, observed_at};
  return true;
}

std::size_t LocalMap::Merge(const MapUpdatePayload &payload) {
  std::size_t changed = 0;
  for (const CellStateUpdate &update : payload.discovered_cells) {
    if (ObserveCell(update.position, update.state, update.observed_at)) {
      ++changed;
    }
  }
  return changed;
}

int LocalMap::IndexOf(Vec3i position) const {
  return (position.z * bounds_.height + position.y) * bounds_.width +
         position.x;
}

} // namespace drone
