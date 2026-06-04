#include "frontier.h"

#include <cmath>
#include <cstdint>
#include <vector>

namespace {

bool IsValidDroneCellState(std::int32_t state) {
  return state == DRONE_CELL_UNKNOWN || state == DRONE_CELL_FREE ||
         state == DRONE_CELL_BLOCKED || state == DRONE_CELL_TARGET;
}

bool IsFiniteAndNonNegative(float value) {
  return std::isfinite(value) && value >= 0.0F;
}

bool IsValidSettings(const DroneFrontierScoringSettings &settings) {
  return IsFiniteAndNonNegative(settings.travel_cost_weight) &&
         IsFiniteAndNonNegative(settings.information_gain_weight) &&
         settings.information_gain_radius >= 0 &&
         IsFiniteAndNonNegative(settings.recent_goal_penalty) &&
         IsFiniteAndNonNegative(settings.same_goal_penalty) &&
         IsFiniteAndNonNegative(settings.nearby_drone_penalty_radius);
}

drone::Vec3i ToVec3i(DroneVec3i point) { return {point.x, point.y, point.z}; }

DroneVec3i ToDroneVec3i(drone::Vec3i point) {
  return {point.x, point.y, point.z};
}

drone::algorithms::frontier::FrontierScoringSettings
ToSettings(const DroneFrontierScoringSettings &settings) {
  return {
      settings.travel_cost_weight,      settings.information_gain_weight,
      settings.information_gain_radius, settings.recent_goal_penalty,
      settings.same_goal_penalty,       settings.nearby_drone_penalty_radius};
}

} // namespace

extern "C" std::int32_t DroneRankFrontierCandidates(
    std::int32_t width, std::int32_t height, std::int32_t depth,
    DroneVec3i start, const DroneVec3i *known_cells,
    const std::int32_t *known_states, std::int32_t known_count,
    const DroneVec3i *recent_goal_cells, std::int32_t recent_goal_count,
    const DroneVec3i *occupied_goal_cells, std::int32_t occupied_goal_count,
    DroneFrontierScoringSettings settings,
    DroneFrontierCandidate *out_candidates, std::int32_t out_capacity) {
  const drone::GridBounds bounds{width, height, depth};
  if (bounds.CellCount() == 0 || known_count < 0 || recent_goal_count < 0 ||
      occupied_goal_count < 0 || out_capacity < 0 ||
      !IsValidSettings(settings)) {
    return 0;
  }
  if ((known_count > 0 &&
       (known_cells == nullptr || known_states == nullptr)) ||
      (recent_goal_count > 0 && recent_goal_cells == nullptr) ||
      (occupied_goal_count > 0 && occupied_goal_cells == nullptr)) {
    return 0;
  }

  drone::algorithms::frontier::FrontierRankingRequest request{
      bounds, ToVec3i(start), {}, {}, {}, ToSettings(settings)};
  request.discovered_cells.reserve(static_cast<std::size_t>(known_count));
  for (std::int32_t i = 0; i < known_count; ++i) {
    if (!IsValidDroneCellState(known_states[i]))
      return 0;
    request.discovered_cells.push_back(
        {ToVec3i(known_cells[i]),
         static_cast<drone::CellState>(known_states[i])});
  }

  request.recent_goal_cells.reserve(
      static_cast<std::size_t>(recent_goal_count));
  for (std::int32_t i = 0; i < recent_goal_count; ++i) {
    request.recent_goal_cells.push_back(ToVec3i(recent_goal_cells[i]));
  }
  request.occupied_goal_cells.reserve(
      static_cast<std::size_t>(occupied_goal_count));
  for (std::int32_t i = 0; i < occupied_goal_count; ++i) {
    request.occupied_goal_cells.push_back(ToVec3i(occupied_goal_cells[i]));
  }

  const std::vector<drone::algorithms::frontier::RankedFrontierCandidate>
      candidates = drone::algorithms::frontier::RankFrontierCandidates(request);
  const auto required = static_cast<std::int32_t>(candidates.size());
  if (required == 0)
    return 0;
  if (out_candidates == nullptr || out_capacity < required)
    return required;

  for (std::int32_t i = 0; i < required; ++i) {
    out_candidates[i] = {ToDroneVec3i(candidates[i].cell), candidates[i].score,
                         candidates[i].raw_distance_squared};
  }
  return required;
}
