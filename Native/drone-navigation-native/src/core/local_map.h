#pragma once

#include "path.h"

#include <optional>
#include <vector>

namespace drone {

struct MapUpdatePayload {
  std::vector<CellStateUpdate> discovered_cells;
};

class LocalMap {
public:
  explicit LocalMap(GridBounds bounds);

  [[nodiscard]] GridBounds bounds() const;
  [[nodiscard]] std::optional<CellStateUpdate> GetCell(Vec3i position) const;
  [[nodiscard]] std::vector<CellStateUpdate> DiscoveredCells() const;
  [[nodiscard]] MapUpdatePayload
  ExtractUpdatesSince(std::int64_t timestamp) const;

  bool ObserveCell(Vec3i position, CellState state, std::int64_t observed_at);
  std::size_t Merge(const MapUpdatePayload &payload);

private:
  [[nodiscard]] int IndexOf(Vec3i position) const;

  GridBounds bounds_;
  std::vector<std::optional<CellStateUpdate>> cells_;
};

} // namespace drone
