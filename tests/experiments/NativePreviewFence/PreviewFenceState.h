// SPDX-License-Identifier: GPL-2.0-only
#pragma once

#include <functional>
#include <map>
#include <optional>
#include <string>
#include <thread>

namespace nian::preview {
enum class Result { Unknown, Active, Sealed, Rejected };
struct Identity { std::string principal, grant, target; };
class FenceState {
public:
  FenceState(std::string incarnation, unsigned capacity);
  FenceState(const FenceState&) = delete;
  FenceState& operator=(const FenceState&) = delete;
  Result begin(const std::string& incarnation, const std::string& operation, const Identity& identity, const std::function<bool()>& action);
  Result seal(const std::string& incarnation, const std::string& operation, const Identity& identity, const std::function<bool()>& action);
private:
  struct Entry {
    Identity identity;
    bool beginPending=false, beginUnknown=false, fenced=false;
    bool returnPending=false, returnUnknown=false, returned=false;
  };
  bool valid(const std::string& incarnation, const std::string& operation, const Identity& identity) const;
  static bool matches(const Identity& left, const Identity& right);
  const std::string incarnation_;
  const unsigned capacity_;
  const std::thread::id ownerThread_;
  std::map<std::string,Entry> entries_;
  std::optional<std::string> owner_;
};
}
