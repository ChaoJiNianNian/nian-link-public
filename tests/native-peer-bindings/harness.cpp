#include "deskflow/control/PeerIdentityBindings.h"
#include <QCoreApplication>
#include <QFile>
#include <QTemporaryDir>

int main(int argc, char **argv)
{
  QCoreApplication app(argc, argv);
  QTemporaryDir temp;
  if (!temp.isValid()) return 1;
  const auto path = temp.filePath("bindings.json");
  auto write = [&](const QByteArray &bytes) {
    QFile file(path);
    return file.open(QIODevice::WriteOnly | QIODevice::Truncate) && file.write(bytes) == bytes.size();
  };
  const auto one = QByteArray(64, 'A');
  const auto two = QByteArray(64, 'B');
  if (PeerIdentityBindings::allows(path, "Win-Two", one)) return 2;
  if (!write("{\"version\":1,\"peers\":[{\"name\":\"Win-Two\",\"fingerprint\":\"" + one +
             "\"},{\"name\":\"Mac-Two\",\"fingerprint\":\"" + two + "\"}]}")) return 3;
  if (!PeerIdentityBindings::allows(path, "Win-Two", one)) return 4;
  if (!PeerIdentityBindings::allows(path, "win-two", one.toLower())) return 5;
  if (PeerIdentityBindings::allows(path, "Win-Two", two)) return 6;
  if (PeerIdentityBindings::allows(path, "Unknown", one)) return 7;
  if (PeerIdentityBindings::allows(path, "Mac-Two", QByteArray())) return 8;
  if (!write("{\"version\":1,\"peers\":[{\"name\":\"Win-Two\",\"fingerprint\":\"" + one +
             "\"},{\"name\":\"win-two\",\"fingerprint\":\"" + two + "\"}]}")) return 9;
  if (PeerIdentityBindings::allows(path, "Win-Two", one)) return 10;
  if (!write("{\"version\":1,\"peers\":[{\"name\":\"Win-Two\",\"fingerprint\":\"" + one +
             "\"},{\"name\":\"Mac-Two\",\"fingerprint\":\"" + one + "\"}]}")) return 11;
  if (PeerIdentityBindings::allows(path, "Win-Two", one)) return 12;
  if (!write("{\"version\":2,\"peers\":[]}")) return 13;
  if (PeerIdentityBindings::allows(path, "Win-Two", one)) return 14;
  return 0;
}
