// SPDX-License-Identifier: GPL-2.0-only
#include "PreviewFenceState.h"
#include <stdexcept>
#include <utility>
namespace nian::preview {
FenceState::FenceState(std::string incarnation, unsigned capacity)
  : incarnation_(std::move(incarnation)), capacity_(capacity), ownerThread_(std::this_thread::get_id()) {
  if (incarnation_.empty() || incarnation_.size()>128 || capacity_==0) throw std::invalid_argument("invalid fence configuration");
}
bool FenceState::valid(const std::string& incarnation, const std::string& operation, const Identity& identity) const {
  return std::this_thread::get_id()==ownerThread_ && incarnation==incarnation_ && !operation.empty() && operation.size()<=128 &&
    !identity.principal.empty() && identity.principal.size()<=128 && !identity.grant.empty() && identity.grant.size()<=128 &&
    !identity.target.empty() && identity.target.size()<=128;
}
bool FenceState::matches(const Identity& a, const Identity& b) {
  return a.principal==b.principal && a.grant==b.grant && a.target==b.target;
}
Result FenceState::begin(const std::string& incarnation, const std::string& operation, const Identity& identity, const std::function<bool()>& action) {
  if (!valid(incarnation,operation,identity)) return Result::Rejected;
  if (auto it=entries_.find(operation);it!=entries_.end()) {
    const auto& e=it->second;
    if (!matches(e.identity,identity) || e.fenced) return Result::Rejected;
    return e.beginPending || e.beginUnknown ? Result::Unknown : Result::Active;
  }
  if (owner_.has_value() || entries_.size()>=capacity_) return Result::Rejected;
  auto [it,inserted]=entries_.emplace(operation,Entry{identity});
  if (!inserted) return Result::Rejected;
  auto& entry=it->second;
  entry.beginPending=true;
  owner_=operation;
  bool settled=false;
  try { settled=action(); } catch (...) { /* A lost reply may still have caused an action. */ }
  entry.beginPending=false;
  entry.beginUnknown=!settled;
  return settled && !entry.fenced ? Result::Active : Result::Unknown;
}
Result FenceState::seal(const std::string& incarnation, const std::string& operation, const Identity& identity, const std::function<bool()>& action) {
  if (!valid(incarnation,operation,identity)) return Result::Rejected;
  auto it=entries_.find(operation);
  if (it==entries_.end()) {
    if (entries_.size()>=capacity_) return Result::Unknown;
    Entry entry{identity};entry.fenced=true;entry.returned=true;
    entries_.emplace(operation,std::move(entry));
    return Result::Sealed;
  }
  auto& entry=it->second;
  if (!matches(entry.identity,identity)) return Result::Rejected;
  entry.fenced=true;
  if (entry.returned) return Result::Sealed;
  if (entry.beginPending || entry.beginUnknown || entry.returnPending || entry.returnUnknown) return Result::Unknown;
  entry.returnPending=true;
  bool returned=false;
  try { returned=action(); } catch (...) { /* Failure may mean the return still executes later. */ }
  entry.returnPending=false;
  if (!returned) { entry.returnUnknown=true; return Result::Unknown; }
  entry.returned=true;
  if (owner_ && *owner_==operation) owner_.reset();
  return Result::Sealed;
}
}
