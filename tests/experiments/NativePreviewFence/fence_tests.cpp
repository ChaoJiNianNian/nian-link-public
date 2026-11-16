#include "PreviewFenceState.h"
#include <functional>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
using namespace nian::preview;
using namespace std::string_literals;

static void check(bool v, const char* what) { if (!v) throw std::runtime_error(what); }
static const Identity a{"A", "g1", "Host"}, b{"B", "g1", "Host"}, other{"A", "g1", "Peer"}, replacement{"A", "g2", "Host"};
static const auto yes=[] {return true;};
static void seal_before_begin() {
  FenceState f("i",8); int actions=0;
  check(f.seal("i","op",a,yes)==Result::Sealed,"seal first");
  check(f.begin("i","op",a,[&]{++actions;return true;})==Result::Rejected,"late begin");
  check(actions==0,"late action executed");
}
static void normal_and_idempotent() {
  FenceState f("i",8); int begins=0,returns=0;
  auto begin=[&]{++begins;return true;}; auto ret=[&]{++returns;return true;};
  check(f.begin("i","op",a,begin)==Result::Active,"begin");
  check(f.begin("i","op",a,begin)==Result::Active && begins==1,"begin replay");
  check(f.seal("i","op",a,ret)==Result::Sealed,"seal");
  check(f.seal("i","op",a,ret)==Result::Sealed && returns==1,"seal replay");
  check(f.begin("i","op2",b,begin)==Result::Active,"next owner");
  check(f.seal("i","op",a,ret)==Result::Sealed && returns==1,"old seal cannot return new owner");
}
static void pending_begin() {
  FenceState f("i",8);int returns=0;
  auto result=f.begin("i","op",a,[&] {
    check(f.seal("i","op",a,[&]{++returns;return true;})==Result::Unknown,"pending begin seal");
    check(f.begin("i","new",b,yes)==Result::Rejected,"other owner during pending");return true;
  });
  check(result==Result::Unknown && returns==0,"pending action cannot prove return");
  check(f.seal("i","op",a,[&]{++returns;return true;})==Result::Sealed && returns==1,"settled begin then return");
  check(f.begin("i","new",b,yes)==Result::Active,"new owner after return");
}
static void uncertain_begin_and_exception() {
  for(bool throws:{false,true}) {
    FenceState f("i",8);int returns=0;
    check(f.begin("i","op",a,[&]() -> bool {if(throws)throw std::runtime_error("lost");return false;})==Result::Unknown,"ambiguous begin");
    check(f.seal("i","op",a,[&]{++returns;return true;})==Result::Unknown && returns==0,"ambiguous begin cannot be sealed");
    check(f.begin("i","new",b,yes)==Result::Rejected,"ambiguous begin blocks owner");
  }
}
static void uncertain_return() {
  for(bool throws:{false,true}) {
    FenceState f("i",8);check(f.begin("i","op",a,yes)==Result::Active,"begin");int returns=0;
    check(f.seal("i","op",a,[&]() -> bool {++returns;if(throws)throw std::runtime_error("lost");return false;})==Result::Unknown,"unknown return");
    check(f.seal("i","op",a,[&]{++returns;return true;})==Result::Unknown && returns==1,"no blind retry");
    check(f.begin("i","new",b,yes)==Result::Rejected,"unknown return blocks");
  }
}
static void pending_return_reentry() {
  FenceState f("i",8); check(f.begin("i","op",a,yes)==Result::Active,"begin");int returns=0;
  check(f.seal("i","op",a,[&] {
    ++returns;check(f.seal("i","op",a,yes)==Result::Unknown,"return in flight");
    check(f.begin("i","new",b,yes)==Result::Rejected,"new owner during return");return true;
  })==Result::Sealed && returns==1,"one return");
}
static void identity_binding() {
  FenceState f("i",8);check(f.begin("i","op",a,yes)==Result::Active,"begin");int calls=0;
  for(const auto& i:{b,other,replacement}) {
    check(f.begin("i","op",i,[&]{++calls;return true;})==Result::Rejected,"bad identity begin");
    check(f.seal("i","op",i,[&]{++calls;return true;})==Result::Rejected,"bad identity seal");
  }
  check(calls==0 && f.seal("i","op",a,yes)==Result::Sealed,"bad identity mutated owner");
}
static void capacity_and_epoch() {
  FenceState f("new",1);int calls=0;
  check(f.seal("old","x",a,[&]{++calls;return true;})==Result::Rejected,"old incarnation");
  check(f.begin("old","x",a,[&]{++calls;return true;})==Result::Rejected,"old begin");
  check(f.seal("new","x",a,yes)==Result::Sealed,"tombstone");
  check(f.seal("new","y",a,yes)==Result::Unknown,"full seal");
  check(f.begin("new","y",a,yes)==Result::Rejected && calls==0,"full begin");
  check(f.begin("new","x",a,yes)==Result::Rejected,"never prune tombstone");
}
static void reject_invalid() {
  FenceState f("i",8);int calls=0;auto action=[&]{++calls;return true;};
  for(const auto& i:{Identity{"","g","t"},Identity{"p","","t"},Identity{"p","g",""}}) {
    check(f.begin("i","op",i,action)==Result::Rejected,"invalid begin");
    check(f.seal("i","op",i,action)==Result::Rejected,"invalid seal");
  }
  check(f.begin("i","",a,action)==Result::Rejected && calls==0,"empty operation");
}
static void rejects_other_thread() {
  FenceState f("i",8); Result outside=Result::Unknown;int calls=0;
  std::thread worker([&] { outside=f.begin("i","op",a,[&] {++calls;return true;}); });
  worker.join();
  check(outside==Result::Rejected && calls==0,"other thread must not mutate state");
  check(f.begin("i","op",a,yes)==Result::Active,"owner thread remains valid");
}
static void stale_incarnation_does_not_release_active() {
  FenceState f("current",8);int returns=0;
  check(f.begin("current","op",a,yes)==Result::Active,"begin");
  check(f.seal("old","op",a,[&]{++returns;return true;})==Result::Rejected && returns==0,"old seal");
  check(f.begin("current","new",b,yes)==Result::Rejected,"old seal released owner");
  check(f.seal("current","op",a,[&]{++returns;return true;})==Result::Sealed && returns==1,"valid return");
}
int main() {
  const std::vector<std::pair<const char*,std::function<void()>>> tests={{"seal_before_begin",seal_before_begin},{"normal_and_idempotent",normal_and_idempotent},{"pending_begin",pending_begin},{"uncertain_begin_and_exception",uncertain_begin_and_exception},{"uncertain_return",uncertain_return},{"pending_return_reentry",pending_return_reentry},{"identity_binding",identity_binding},{"capacity_and_epoch",capacity_and_epoch},{"reject_invalid",reject_invalid},{"rejects_other_thread",rejects_other_thread},{"stale_incarnation_does_not_release_active",stale_incarnation_does_not_release_active}};
  int failures=0;
  for(const auto& [name,test]:tests) try {test();std::cout<<"PASS "<<name<<'\n';} catch(const std::exception& e) {++failures;std::cerr<<"FAIL "<<name<<": "<<e.what()<<'\n';}
  std::cout<<tests.size()-failures<<"/"<<tests.size()<<" passed\n";return failures?1:0;
}
