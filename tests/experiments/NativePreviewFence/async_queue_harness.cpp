#include "PreviewFenceState.h"
#include "arch/Arch.h"
#include "base/Event.h"
#include "base/EventQueue.h"
#include "base/Log.h"
#include <QCoreApplication>
#include <iostream>
#include <memory>
#include <string>
#include <vector>

using namespace nian::preview;
struct Command final : EventData { explicit Command(int n) : number(n) {} int number; };

int main(int argc, char** argv) {
  if (argc != 2) return 2;
  const std::string mode = argv[1];
  if (mode != "async-true" && mode != "async-unknown" && mode != "sync") return 2;
  QCoreApplication app(argc, argv);
  Arch arch;
#if defined(Q_OS_WIN)
  arch.init();
#endif
  Log log;
  EventQueue queue;
  std::unique_ptr<FenceState> fence;
  const Identity identity{"synthetic-peer", "synthetic-grant", "synthetic-host"};
  std::vector<std::string> trace;
  Result beginResult = Result::Rejected, sealResult = Result::Rejected;
  bool onHost = true;
  int lateActions = 0, returnActions = 0;
  auto addCommand = [&](int number) {
    queue.addEvent(Event(EventTypes::DeskToolboxControlRequest, &queue, new Command(number)));
  };
  queue.addHandler(EventTypes::DeskToolboxControlRequest, &queue, [&](const Event& event) {
    if (!fence) fence = std::make_unique<FenceState>("synthetic-incarnation", 8);
    const auto command = static_cast<const Command*>(event.getDataObject())->number;
    if (command == 1) {
      trace.push_back("begin");
      beginResult = fence->begin("synthetic-incarnation", "synthetic-operation", identity, [&] {
        if (mode == "sync") onHost = false;
        else addCommand(3); // Only a synthetic action is deferred, on this same queue.
        return mode != "async-unknown";
      });
    } else if (command == 2) {
      trace.push_back("seal");
      sealResult = fence->seal("synthetic-incarnation", "synthetic-operation", identity, [&] {
        ++returnActions;
        onHost = true;
        return true;
      });
      if (mode == "sync") queue.addEvent(Event(EventTypes::Quit));
    } else if (command == 3) {
      trace.push_back("late");
      ++lateActions;
      onHost = false;
      queue.addEvent(Event(EventTypes::Quit));
    }
  });
  // Begin and Seal precede the action that Begin itself appends while running.
  addCommand(1);
  addCommand(2);
  const auto exitCode = queue.loop();
  const auto expectedTrace = mode == "sync" ? std::vector<std::string>{"begin", "seal"}
                                             : std::vector<std::string>{"begin", "seal", "late"};
  if (exitCode != 0 || trace != expectedTrace) {
    std::cerr << "FAIL synthetic event ordering\n";
    return 1;
  }
  if (mode == "async-true") {
    if (beginResult != Result::Active || sealResult != Result::Sealed || onHost ||
        lateActions != 1 || returnActions != 1) {
      std::cerr << "FAIL expected unsafe synthetic counterexample was not observed\n";
      return 1;
    }
    std::cout << "RED synthetic contract misuse: begin=Active seal=Sealed trace=begin,seal,late final=Peer late=1 returns=1\n";
    return 23; // Exact expected red: the claimed sealed state did not prevent the late action.
  }
  if (mode == "async-unknown") {
    if (beginResult != Result::Unknown || sealResult != Result::Unknown || onHost ||
        lateActions != 1 || returnActions != 0) {
      std::cerr << "FAIL synthetic unknown control\n";
      return 1;
    }
    std::cout << "PASS synthetic unknown control: begin=Unknown seal=Unknown trace=begin,seal,late final=Peer late=1 returns=0\n";
    return 0;
  }
  if (beginResult != Result::Active || sealResult != Result::Sealed || !onHost ||
      lateActions != 0 || returnActions != 1) {
    std::cerr << "FAIL synchronous synthetic control\n";
    return 1;
  }
  std::cout << "PASS synchronous synthetic control: begin=Active seal=Sealed trace=begin,seal final=Host late=0 returns=1\n";
  return 0;
}
