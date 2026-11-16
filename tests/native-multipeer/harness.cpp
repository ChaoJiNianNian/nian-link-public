#include "deskflow/control/DeskToolboxControl.h"
#include <QCoreApplication>
#include <QJsonArray>
#include <QTextStream>
#include <QTimer>

// Transport test only: no real desktop or keyboard hooks are installed.
int main(int argc, char **argv)
{
  QCoreApplication app(argc, argv);
  QString active = QStringLiteral("Host");
  qint64 revision = 0;
  DeskToolboxControl control([&](QJsonObject request, DeskToolboxControl::Complete complete) {
    const auto action = request.value("action").toString();
    bool ok = true;
    QString error;
    if (action == "switch-if-current") {
      if (request.value("expectedActive").toString() != active ||
          request.value("expectedRevision").toInteger(-1) != revision) {
        ok = false;
        error = QStringLiteral("focus_changed");
      } else if (request.value("target").toString() != active) {
        active = request.value("target").toString();
        ++revision;
      }
    }
    complete({{"ok", ok}, {"activeComputer", active}, {"connectedComputers", QJsonArray{}},
              {"revision", revision}, {"error", error}});
  });
  if (!control.listen()) return 2;
  QTextStream(stdout) << control.endpoint() << Qt::endl;
  QTimer::singleShot(60000, &app, &QCoreApplication::quit);
  return app.exec();
}
