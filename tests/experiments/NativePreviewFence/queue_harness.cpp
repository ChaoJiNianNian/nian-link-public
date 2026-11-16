#include "PreviewFenceState.h"
#include "arch/Arch.h"
#include "base/Event.h"
#include "base/EventQueue.h"
#include "base/Log.h"
#include <QCoreApplication>
#include <iostream>
#include <memory>
#include <vector>

using namespace nian::preview;
struct Command : EventData { explicit Command(int n):number(n) {} int number; };
int main(int argc, char** argv) {
  QCoreApplication app(argc,argv);
  Arch arch;
#if defined(Q_OS_WIN)
  arch.init();
#endif
  Log log;
  EventQueue queue;
  std::unique_ptr<FenceState> fence;
  std::vector<Result> results;
  int beginActions=0,returnActions=0;
  const Identity identity{"fixture-peer","fixture-grant","fixture-host"};
  queue.addHandler(EventTypes::DeskToolboxControlRequest,&queue,[&](const Event& event) {
    if (!fence) fence=std::make_unique<FenceState>("fixture-incarnation",8);
    const auto command=static_cast<const Command*>(event.getDataObject())->number;
    const auto begin=[&]{++beginActions;return true;};
    const auto returned=[&]{++returnActions;return true;};
    if(command==1) results.push_back(fence->begin("fixture-incarnation","first",identity,begin));
    if(command==2) results.push_back(fence->seal("fixture-incarnation","first",identity,returned));
    if(command==3) results.push_back(fence->seal("fixture-incarnation","late",identity,returned));
    if(command==4) results.push_back(fence->begin("fixture-incarnation","late",identity,begin));
  });
  // Queue before loop; EventQueue moves its own pending events to the real buffer.
  // Avoid waitForReady's missed-notification race in this standalone fixture.
  for(int i=1;i<=4;i++) queue.addEvent(Event(EventTypes::DeskToolboxControlRequest,&queue,new Command(i)));
  queue.addEvent(Event(EventTypes::Quit));
  const auto exitCode=queue.loop();
  const std::vector<Result> expected{Result::Active,Result::Sealed,Result::Sealed,Result::Rejected};
  if(exitCode!=0 || results!=expected || beginActions!=1 || returnActions!=1) {
    std::cerr<<"FAIL real EventQueue fixture\n"; return 1;
  }
  std::cout<<"PASS real EventQueue fixture; actions are synthetic\n";
  return 0;
}
