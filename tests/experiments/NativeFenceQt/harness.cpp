#include "deskflow/control/DeskToolboxControl.h"
#include "deskflow/control/PreviewFenceState.h"
#include "arch/Arch.h"
#include "base/Event.h"
#include "base/EventQueue.h"
#include "base/Log.h"
#include <QCoreApplication>
#include <QJsonArray>
#include <QMetaObject>
#include <QTextStream>
#include <QTimer>
#include <atomic>
#include <chrono>
#include <future>
#include <memory>
#include <optional>
#include <thread>

using nian::preview::FenceState;
using nian::preview::Identity;
using nian::preview::Result;
struct ControlEvent final : EventData {
  bool ready = false;
  QJsonObject request;
  DeskToolboxControl::Complete complete;
};
struct HeldRequest { QJsonObject request; DeskToolboxControl::Complete complete; };

int main(int argc, char** argv)
{
  QCoreApplication app(argc, argv);
  qputenv("DESKTOOLBOX_CONTROL", "1");
  Arch arch;
#if defined(Q_OS_WIN)
  arch.init();
#endif
  Log log;
  EventQueue queue;
  std::promise<void> ready;
  auto readyFuture = ready.get_future();
  std::optional<HeldRequest> held; // Qt thread only.
  QSet<QString> heldOnce;           // Qt thread only.
  std::atomic<int> begins{0}, returns{0};
  DeskToolboxControl* controlPtr = nullptr;

  auto enqueue = [&](QJsonObject request, DeskToolboxControl::Complete complete) {
    auto* event = new ControlEvent();
    event->request = std::move(request); event->complete = std::move(complete);
    queue.addEvent(Event(EventTypes::DeskToolboxControlRequest, &queue, event));
  };
  queue.addHandler(EventTypes::DeskToolboxControlRequest, &queue, [&](const Event& event) {
    auto* item = static_cast<const ControlEvent*>(event.getDataObject());
    if (item->ready) { ready.set_value(); return; }
    // The state is created and destroyed on the real EventQueue thread.
    thread_local std::unique_ptr<FenceState> fence;
    if (!fence) fence = std::make_unique<FenceState>("11111111111111111111111111111111", 8);
    const auto requestId = item->request.value("requestId").toString();
    const auto operation = item->request.value("operationId").toString();
    const auto incarnation = item->request.value("incarnation").toString();
    const auto action = item->request.value("action").toString();
    const auto target = item->request.value("target").toString();
    // requestId mapping is only a synthetic fixture identity; it is NOT authentication.
    const Identity identity{requestId.startsWith('b') ? "synthetic-b" : "synthetic-a", "synthetic-grant", target.toStdString()};
    Result result = Result::Rejected;
    if (action == "fence-begin") result = fence->begin(incarnation.toStdString(), operation.toStdString(), identity,
        [&] { ++begins; return true; });
    if (action == "fence-seal") result = fence->seal(incarnation.toStdString(), operation.toStdString(), identity,
        [&] { ++returns; return true; });
    const char* label = result == Result::Active ? "active" : result == Result::Sealed ? "sealed" :
                        result == Result::Unknown ? "unconfirmed" : "rejected";
    item->complete({{"ok", result != Result::Rejected}, {"result", label},
                    {"syntheticBegins", begins.load()}, {"syntheticReturns", returns.load()}});
    if (action == "fence-seal") {
      QMetaObject::invokeMethod(controlPtr, [&, operation] {
        if (held && held->request.value("operationId").toString() == operation) {
          auto old = std::move(*held); held.reset(); enqueue(std::move(old.request), std::move(old.complete));
        }
      }, Qt::QueuedConnection);
    }
  });
  auto* marker = new ControlEvent(); marker->ready = true;
  queue.addEvent(Event(EventTypes::DeskToolboxControlRequest, &queue, marker));
  std::thread worker([&] { queue.loop(); });
  if (readyFuture.wait_for(std::chrono::seconds(3)) != std::future_status::ready) {
    queue.addEvent(Event(EventTypes::Quit)); worker.join(); return 3;
  }

  DeskToolboxControl control([&](QJsonObject request, DeskToolboxControl::Complete complete) {
    const auto action = request.value("action").toString();
    if (action == "state") {
      complete({{"ok", true}, {"activeComputer", "Host"}, {"connectedComputers", QJsonArray{}}, {"revision", 0}});
      return;
    }
    if (action == "fixture-exit") {
      complete({{"ok", true}});
      QTimer::singleShot(100, &app, &QCoreApplication::quit);
      return;
    }
    if (action == "switch-if-current" || action == "switch") {
      // Baseline v1 proof only; no native state or keyboard is touched.
      complete({{"ok", true}, {"activeComputer", "Host"}, {"connectedComputers", QJsonArray{}}, {"revision", 0}});
      return;
    }
    const auto operation = request.value("operationId").toString();
    // Deliberately never complete e-prefixed fixture requests: exercise the
    // real Qt per-connection pending limit without touching an OS backend.
    if (request.value("requestId").toString().startsWith('e')) return;
    // d-prefixed fixture requests exercise a normal Begin -> Seal order.
    if (action == "fence-begin" && !request.value("requestId").toString().startsWith('d') && !heldOnce.contains(operation)) {
      if (held) { complete({{"ok", false}, {"result", "fixture_busy"}}); return; }
      heldOnce.insert(operation); held = HeldRequest{std::move(request), std::move(complete)};
      QTextStream(stdout) << "BEGIN_HELD" << Qt::endl;
      return;
    }
    enqueue(std::move(request), std::move(complete));
  });
  controlPtr = &control;
  if (!control.listen()) { queue.addEvent(Event(EventTypes::Quit)); worker.join(); return 4; }
  QTextStream(stdout) << control.endpoint() << Qt::endl;
  QTimer::singleShot(30000, &app, &QCoreApplication::quit);
  const auto code = app.exec();
  queue.addEvent(Event(EventTypes::Quit));
  worker.join();
  return code;
}
