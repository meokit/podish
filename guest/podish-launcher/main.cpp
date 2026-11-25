#include <QApplication>
#include <QColor>
#include <QComboBox>
#include <QDateTime>
#include <QDir>
#include <QElapsedTimer>
#include <QFile>
#include <QFileInfo>
#include <QFont>
#include <QFontMetrics>
#include <QFrame>
#include <QGuiApplication>
#include <QHBoxLayout>
#include <QIcon>
#include <QImage>
#include <QInputMethod>
#include <QInputMethodEvent>
#include <QInputMethodQueryEvent>
#include <QJsonDocument>
#include <QJsonObject>
#include <QKeyEvent>
#include <QLabel>
#include <QLineEdit>
#include <QListView>
#include <QMap>
#include <QMouseEvent>
#include <QOpenGLContext>
#include <QOpenGLWindow>
#include <QPainter>
#include <QPainterPath>
#include <QPalette>
#include <QPen>
#include <QPixmap>
#include <QPointer>
#include <QProcess>
#include <QProcessEnvironment>
#include <QPushButton>
#include <QRectF>
#include <QResizeEvent>
#include <QSaveFile>
#include <QScrollArea>
#include <QScroller>
#include <QSet>
#include <QStandardPaths>
#include <QStyledItemDelegate>
#include <QSurfaceFormat>
#include <QTabWidget>
#include <QTextStream>
#include <QTimer>
#include <QVBoxLayout>
#include <QWheelEvent>
#include <QWidget>

#include <dlfcn.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <memory>
#include <string>
#include <unordered_map>

namespace {

struct AppEntry {
    QString id;
    QString name;
    QString comment;
    QString iconName;
    QString execLine;
    QString workingDir;
    QString category;
    bool terminal = false;
    bool pinned = false;
    int pinOrder = 999;
};

static const QStringList kPinnedIds = {
    QStringLiteral("qterminal"),
    QStringLiteral("pcmanfm-qt"),
    QStringLiteral("featherpad"),
    QStringLiteral("lximage-qt"),
    QStringLiteral("lxqt-archiver"),
    QStringLiteral("qps"),
    QStringLiteral("lxqt-config"),
    QStringLiteral("screengrab"),
    QStringLiteral("speedcrunch"),
    QStringLiteral("galculator"),
    QStringLiteral("supertuxkart"),
    QStringLiteral("lxqt-config-appearance"),
    QStringLiteral("lxqt-config-file-associations"),
};

static const QStringList kFallbackSearchPaths = {
    QStringLiteral("/usr/local/sbin"),  QStringLiteral("/usr/local/bin"), QStringLiteral("/usr/sbin"),
    QStringLiteral("/usr/bin"),         QStringLiteral("/sbin"),          QStringLiteral("/bin"),
    QStringLiteral("/usr/local/games"), QStringLiteral("/usr/games"),
};

static QString resolveExecutablePath(const QString& program) {
    if (program.isEmpty())
        return {};
    if (program.contains(QLatin1Char('/'))) {
        QFileInfo fi(program);
        return (fi.exists() && fi.isExecutable()) ? fi.absoluteFilePath() : QString();
    }
    QString found = QStandardPaths::findExecutable(program);
    if (!found.isEmpty())
        return found;
    return QStandardPaths::findExecutable(program, kFallbackSearchPaths);
}

static QString compositorConfigPath() {
    QString cfgRoot = QProcessEnvironment::systemEnvironment().value(QStringLiteral("XDG_CONFIG_HOME"));
    if (cfgRoot.isEmpty()) {
        cfgRoot = QDir::homePath() + QStringLiteral("/.config");
    }
    return cfgRoot + QStringLiteral("/podish/compositor.json");
}

static bool sendCompositorControl(const QJsonObject& req, QJsonObject* replyOut = nullptr) {
    int fd = ::socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0)
        return false;

    sockaddr_un addr{};
    addr.sun_family = AF_UNIX;
    const char* sockPath = "/run/podish-control.sock";
    if (std::strlen(sockPath) >= sizeof(addr.sun_path)) {
        ::close(fd);
        return false;
    }
    std::strcpy(addr.sun_path, sockPath);

    if (::connect(fd, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) != 0) {
        ::close(fd);
        return false;
    }

    QByteArray line = QJsonDocument(req).toJson(QJsonDocument::Compact);
    line.append('\n');

    const char* ptr = line.constData();
    ssize_t rem = line.size();
    while (rem > 0) {
        ssize_t n = ::send(fd, ptr, static_cast<size_t>(rem), 0);
        if (n <= 0) {
            ::close(fd);
            return false;
        }
        ptr += n;
        rem -= n;
    }

    std::string resp;
    char buf[1024];
    while (true) {
        ssize_t n = ::recv(fd, buf, sizeof(buf), 0);
        if (n <= 0)
            break;
        resp.append(buf, static_cast<size_t>(n));
        if (resp.find('\n') != std::string::npos)
            break;
    }
    ::close(fd);

    if (resp.empty())
        return false;
    const size_t nl = resp.find('\n');
    const QByteArray payload = QByteArray(resp.data(), static_cast<int>(nl == std::string::npos ? resp.size() : nl));
    const QJsonDocument doc = QJsonDocument::fromJson(payload);
    if (!doc.isObject())
        return false;
    if (replyOut)
        *replyOut = doc.object();
    return doc.object().value(QStringLiteral("ok")).toBool(false);
}

static QString classifyCategory(const QStringList& categories, const QString& id) {
    if (id.contains(QStringLiteral("config")) || id.contains(QStringLiteral("settings"))) {
        return QStringLiteral("Settings");
    }
    for (const QString& raw : categories) {
        const QString c = raw.trimmed().toLower();
        if (c == QStringLiteral("settings") || c == QStringLiteral("desktopsettings")) {
            return QStringLiteral("Settings");
        }
        if (c == QStringLiteral("system") || c == QStringLiteral("terminalemulator") ||
            c == QStringLiteral("filemanager") || c == QStringLiteral("monitor")) {
            return QStringLiteral("System");
        }
        if (c == QStringLiteral("graphics") || c == QStringLiteral("photography") || c == QStringLiteral("viewer") ||
            c == QStringLiteral("game")) {
            return QStringLiteral("Graphics");
        }
        if (c == QStringLiteral("development") || c == QStringLiteral("ide")) {
            return QStringLiteral("Development");
        }
        if (c == QStringLiteral("office") || c == QStringLiteral("texteditor")) {
            return QStringLiteral("Utilities");
        }
        if (c == QStringLiteral("utility") || c == QStringLiteral("archiving") || c == QStringLiteral("calculator") ||
            c == QStringLiteral("accessories")) {
            return QStringLiteral("Utilities");
        }
    }
    return QStringLiteral("Utilities");
}

static QStringList splitDesktopExec(const QString& execLine) {
    QStringList out;
    QString current;
    bool inSingle = false;
    bool inDouble = false;
    bool escape = false;

    for (int i = 0; i < execLine.size(); ++i) {
        const QChar ch = execLine[i];
        if (escape) {
            current.append(ch);
            escape = false;
            continue;
        }
        if (ch == QLatin1Char('\\') && !inSingle) {
            escape = true;
            continue;
        }
        if (ch == QLatin1Char('\'') && !inDouble) {
            inSingle = !inSingle;
            continue;
        }
        if (ch == QLatin1Char('"') && !inSingle) {
            inDouble = !inDouble;
            continue;
        }
        if (ch.isSpace() && !inSingle && !inDouble) {
            if (!current.isEmpty()) {
                out.append(current);
                current.clear();
            }
            continue;
        }
        current.append(ch);
    }
    if (!current.isEmpty()) {
        out.append(current);
    }

    QStringList cleaned;
    for (const QString& token : out) {
        if (token.size() == 2 && token[0] == QLatin1Char('%')) {
            const QChar code = token[1];
            if (code == QLatin1Char('f') || code == QLatin1Char('F') || code == QLatin1Char('u') ||
                code == QLatin1Char('U') || code == QLatin1Char('d') || code == QLatin1Char('D') ||
                code == QLatin1Char('n') || code == QLatin1Char('N') || code == QLatin1Char('i') ||
                code == QLatin1Char('c') || code == QLatin1Char('k') || code == QLatin1Char('v') ||
                code == QLatin1Char('m')) {
                continue;
            }
        }
        cleaned.append(token);
    }
    return cleaned;
}

static QIcon makeFallbackIcon(const QString& title) {
    static const QList<QColor> palette = {
        QColor(QStringLiteral("#3daee9")), QColor(QStringLiteral("#1d99f3")), QColor(QStringLiteral("#1abc9c")),
        QColor(QStringLiteral("#8e44ad")), QColor(QStringLiteral("#e67e22")), QColor(QStringLiteral("#e74c3c")),
        QColor(QStringLiteral("#2ecc71")),
    };
    uint hash = 2166136261u;
    for (const QChar c : title) {
        hash ^= c.unicode();
        hash *= 16777619u;
    }
    const QColor bg = palette[hash % palette.size()];

    QPixmap pm(96, 96);
    pm.fill(Qt::transparent);
    QPainter p(&pm);
    p.setRenderHint(QPainter::Antialiasing, true);
    QPainterPath path;
    path.addRoundedRect(4, 4, 88, 88, 24, 24);
    QLinearGradient grad(4, 4, 92, 92);
    grad.setColorAt(0.0, bg.lighter(115));
    grad.setColorAt(1.0, bg.darker(118));
    p.fillPath(path, grad);

    QFont f = QApplication::font();
    f.setPixelSize(40);
    f.setBold(true);
    p.setFont(f);
    p.setPen(Qt::white);
    const QString glyph = title.trimmed().isEmpty() ? QStringLiteral("A") : title.trimmed().left(1).toUpper();
    p.drawText(pm.rect(), Qt::AlignCenter, glyph);
    return QIcon(pm);
}

static QIcon resolveAppIcon(const QString& iconName, const QString& title) {
    if (!iconName.isEmpty()) {
        if (iconName.startsWith(QLatin1Char('/')) && QFileInfo::exists(iconName)) {
            QIcon direct(iconName);
            if (!direct.isNull())
                return direct;
        }
        QIcon themed = QIcon::fromTheme(iconName);
        if (!themed.isNull())
            return themed;

        const QStringList searchDirs = {
            QStringLiteral("/usr/share/icons/breeze/apps/48"),
            QStringLiteral("/usr/share/icons/breeze/apps/64"),
            QStringLiteral("/usr/share/icons/breeze/apps/32"),
            QStringLiteral("/usr/share/icons/hicolor/scalable/apps"),
            QStringLiteral("/usr/share/icons/hicolor/128x128/apps"),
            QStringLiteral("/usr/share/icons/hicolor/64x64/apps"),
            QStringLiteral("/usr/share/icons/hicolor/48x48/apps"),
            QStringLiteral("/usr/share/pixmaps"),
            QStringLiteral("/usr/local/share/icons/hicolor/scalable/apps"),
        };
        const QStringList exts = {QString(), QStringLiteral(".svg"), QStringLiteral(".png")};
        for (const QString& dir : searchDirs) {
            for (const QString& ext : exts) {
                const QString path = dir + QLatin1Char('/') + iconName + ext;
                if (QFileInfo::exists(path)) {
                    QIcon icon(path);
                    if (!icon.isNull())
                        return icon;
                }
            }
        }
    }
    return makeFallbackIcon(title);
}

static QList<AppEntry> scanDesktopEntries() {
    const QStringList dirs = {
        QStringLiteral("/usr/local/share/applications"),
        QStringLiteral("/usr/share/applications"),
        QDir::homePath() + QStringLiteral("/.local/share/applications"),
    };

    QMap<QString, AppEntry> byId;
    for (const QString& dirPath : dirs) {
        QDir dir(dirPath);
        if (!dir.exists())
            continue;
        const QFileInfoList files = dir.entryInfoList({QStringLiteral("*.desktop")}, QDir::Files | QDir::Readable);
        for (const QFileInfo& fi : files) {
            QFile file(fi.absoluteFilePath());
            if (!file.open(QIODevice::ReadOnly | QIODevice::Text))
                continue;

            const QString id = fi.completeBaseName();
            if (id == QStringLiteral("podish-launcher"))
                continue;

            QTextStream in(&file);
            bool inDesktopEntry = false;
            QString type, name, genericName, comment, icon, exec, tryExec, path;
            QStringList categories;
            bool noDisplay = false;
            bool hidden = false;
            bool terminal = false;

            while (!in.atEnd()) {
                const QString line = in.readLine().trimmed();
                if (line.isEmpty() || line.startsWith(QLatin1Char('#')))
                    continue;
                if (line.startsWith(QLatin1Char('['))) {
                    if (inDesktopEntry)
                        break;
                    inDesktopEntry = (line == QStringLiteral("[Desktop Entry]"));
                    continue;
                }
                if (!inDesktopEntry)
                    continue;
                const int eq = line.indexOf(QLatin1Char('='));
                if (eq <= 0)
                    continue;
                const QString key = line.left(eq).trimmed();
                const QString val = line.mid(eq + 1).trimmed();

                if (key == QStringLiteral("Type"))
                    type = val;
                else if (key == QStringLiteral("Name"))
                    name = val;
                else if (key == QStringLiteral("GenericName"))
                    genericName = val;
                else if (key == QStringLiteral("Comment"))
                    comment = val;
                else if (key == QStringLiteral("Icon"))
                    icon = val;
                else if (key == QStringLiteral("Exec"))
                    exec = val;
                else if (key == QStringLiteral("TryExec"))
                    tryExec = val;
                else if (key == QStringLiteral("Path"))
                    path = val;
                else if (key == QStringLiteral("Categories"))
                    categories = val.split(QLatin1Char(';'), Qt::SkipEmptyParts);
                else if (key == QStringLiteral("NoDisplay"))
                    noDisplay = (val.compare(QStringLiteral("true"), Qt::CaseInsensitive) == 0);
                else if (key == QStringLiteral("Hidden"))
                    hidden = (val.compare(QStringLiteral("true"), Qt::CaseInsensitive) == 0);
                else if (key == QStringLiteral("Terminal"))
                    terminal = (val.compare(QStringLiteral("true"), Qt::CaseInsensitive) == 0);
            }

            if (type != QStringLiteral("Application") || noDisplay || hidden || name.isEmpty() || exec.isEmpty()) {
                continue;
            }
            if (!tryExec.isEmpty() && resolveExecutablePath(tryExec).isEmpty()) {
                continue;
            }
            if (id.contains(QStringLiteral("policykit")) || id.contains(QStringLiteral("lxqt-leave")) ||
                id.contains(QStringLiteral("lxqt-hibernate")) || id.contains(QStringLiteral("lxqt-suspend")) ||
                id.contains(QStringLiteral("lxqt-reboot")) || id.contains(QStringLiteral("lxqt-shutdown")) ||
                id.contains(QStringLiteral("lxqt-lockscreen")) || id.contains(QStringLiteral("lxqt-logout"))) {
                continue;
            }

            AppEntry entry;
            entry.id = id;
            entry.name = name;
            entry.comment = comment.isEmpty() ? genericName : comment;
            entry.iconName = icon;
            entry.execLine = exec;
            entry.workingDir = path;
            entry.terminal = terminal;
            entry.category = classifyCategory(categories, id);
            const int pinIdx = kPinnedIds.indexOf(id);
            if (pinIdx >= 0) {
                entry.pinned = true;
                entry.pinOrder = pinIdx;
            }
            byId.insert(id, entry);
        }
    }

    QList<AppEntry> result = byId.values();
    std::sort(result.begin(), result.end(), [](const AppEntry& a, const AppEntry& b) {
        if (a.pinned != b.pinned)
            return a.pinned > b.pinned;
        if (a.pinOrder != b.pinOrder)
            return a.pinOrder < b.pinOrder;
        return a.name.compare(b.name, Qt::CaseInsensitive) < 0;
    });
    return result;
}

static QString readMemorySummary() {
    QFile f(QStringLiteral("/proc/meminfo"));
    if (!f.open(QIODevice::ReadOnly | QIODevice::Text)) {
        return QStringLiteral("Debian i386");
    }
    qint64 totalKb = 0, availKb = 0;
    QTextStream in(&f);
    while (!in.atEnd()) {
        const QString line = in.readLine();
        if (line.startsWith(QStringLiteral("MemTotal:"))) {
            totalKb = line.split(QLatin1Char(' '), Qt::SkipEmptyParts).value(1).toLongLong();
        } else if (line.startsWith(QStringLiteral("MemAvailable:"))) {
            availKb = line.split(QLatin1Char(' '), Qt::SkipEmptyParts).value(1).toLongLong();
        }
    }
    if (totalKb > 0) {
        const qint64 usedMb = std::max<qint64>(0, (totalKb - availKb) / 1024);
        const qint64 totalMb = totalKb / 1024;
        return QStringLiteral("Debian i386  •  %1 / %2 MB").arg(usedMb).arg(totalMb);
    }
    return QStringLiteral("Debian i386");
}

static bool launchDesktopEntry(const AppEntry& app) {
    QStringList args = splitDesktopExec(app.execLine);
    if (args.isEmpty())
        return false;

    QString program = args.takeFirst();
    QString resolved = resolveExecutablePath(program);
    if (resolved.isEmpty())
        return false;
    program = resolved;

    if (app.terminal) {
        QString termBin = resolveExecutablePath(QStringLiteral("qterminal"));
        if (termBin.isEmpty())
            termBin = resolveExecutablePath(QStringLiteral("x-terminal-emulator"));
        if (termBin.isEmpty())
            return false;
        QStringList termArgs;
        termArgs << QStringLiteral("-e") << program;
        termArgs.append(args);
        program = termBin;
        args = termArgs;
    }

    QProcess proc;
    proc.setProgram(program);
    proc.setArguments(args);
    if (!app.workingDir.isEmpty() && QDir(app.workingDir).exists()) {
        proc.setWorkingDirectory(app.workingDir);
    } else {
        proc.setWorkingDirectory(QDir::homePath());
    }
    QProcessEnvironment env = QProcessEnvironment::systemEnvironment();
    const QString curPath = env.value(QStringLiteral("PATH"));
    if (!curPath.contains(QStringLiteral("/usr/games"))) {
        env.insert(QStringLiteral("PATH"), curPath.isEmpty()
                                               ? kFallbackSearchPaths.join(QLatin1Char(':'))
                                               : curPath + QStringLiteral(":/usr/local/games:/usr/games"));
    }
    proc.setProcessEnvironment(env);
    qint64 pid = 0;
    return proc.startDetached(&pid);
}

static QPalette makeDarkPalette() {
    QPalette pal;
    const QColor bg(QStringLiteral("#0f172a"));
    const QColor surface(QStringLiteral("#1e293b"));
    const QColor alt(QStringLiteral("#162033"));
    const QColor text(QStringLiteral("#f8fafc"));
    const QColor muted(QStringLiteral("#94a3b8"));
    const QColor accent(QStringLiteral("#0284c7"));

    pal.setColor(QPalette::Window, bg);
    pal.setColor(QPalette::WindowText, text);
    pal.setColor(QPalette::Base, surface);
    pal.setColor(QPalette::AlternateBase, alt);
    pal.setColor(QPalette::ToolTipBase, surface);
    pal.setColor(QPalette::ToolTipText, text);
    pal.setColor(QPalette::Text, text);
    pal.setColor(QPalette::PlaceholderText, muted);
    pal.setColor(QPalette::Button, surface);
    pal.setColor(QPalette::ButtonText, text);
    pal.setColor(QPalette::BrightText, Qt::white);
    pal.setColor(QPalette::Highlight, accent);
    pal.setColor(QPalette::HighlightedText, Qt::white);
    return pal;
}

static void configureDarkComboBox(QComboBox* combo) {
    const QPalette darkPal = makeDarkPalette();
    combo->setPalette(darkPal);

    auto* view = new QListView(combo);
    view->setUniformItemSizes(true);
    view->setSpacing(2);
    view->setFrameShape(QFrame::NoFrame);
    view->setAutoFillBackground(true);
    view->viewport()->setAutoFillBackground(true);
    view->setPalette(darkPal);
    view->viewport()->setPalette(darkPal);
    view->setStyleSheet(QStringLiteral(R"(
        QListView {
            background-color: #1e293b;
            color: #f8fafc;
            border: 1px solid #38bdf8;
            border-radius: 10px;
            padding: 4px;
            outline: 0;
        }
        QListView::item {
            background-color: #1e293b;
            color: #f8fafc;
            padding: 7px 12px;
            min-height: 28px;
            border-radius: 6px;
        }
        QListView::item:hover {
            background-color: #334155;
            color: #ffffff;
        }
        QListView::item:selected {
            background-color: #0284c7;
            color: #ffffff;
        }
    )"));

    combo->setView(view);
    // Replace Qt's QComboBoxDelegate (which ignores QSS backgrounds under light LXQt/Fusion themes)
    // with QStyledItemDelegate so dropdown items always render cleanly on dark surfaces.
    combo->setItemDelegate(new QStyledItemDelegate(view));

    if (QWidget* container = view->parentWidget()) {
        container->setAutoFillBackground(true);
        container->setPalette(darkPal);
        container->setStyleSheet(QStringLiteral(
            "background-color: #1e293b; color: #f8fafc; border: 1px solid #38bdf8; border-radius: 10px;"));
    }
}

class SettingsWindow : public QWidget {
public:
    SettingsWindow(const QList<AppEntry>& apps, bool mobileMode, QWidget* parent = nullptr)
        : QWidget(parent, Qt::Window)
        , m_apps(apps)
        , m_mobileMode(mobileMode) {
        setWindowTitle(QStringLiteral("Podish Settings"));
        setObjectName(QStringLiteral("SettingsRoot"));
        setAttribute(Qt::WA_DeleteOnClose, true);
        setPalette(makeDarkPalette());
        setMinimumSize(320, 420);
        if (!mobileMode) {
            resize(560, 680);
        }

        buildUi();
        loadState();
    }

    void refreshApps(const QList<AppEntry>& apps) {
        m_apps = apps;
        rebuildAppDpiList();
    }

protected:
    void resizeEvent(QResizeEvent* event) override {
        QWidget::resizeEvent(event);
        positionToast();
    }

private:
    static QList<QPair<QString, int>> dpiChoices(bool includeInherit) {
        QList<QPair<QString, int>> list;
        list.append({includeInherit ? QStringLiteral("Inherit Global") : QStringLiteral("Auto (Balanced)"), 0});
        list.append({QStringLiteral("96 DPI (1.0x)"), 96});
        list.append({QStringLiteral("120 DPI (1.25x)"), 120});
        list.append({QStringLiteral("144 DPI (1.5x)"), 144});
        list.append({QStringLiteral("168 DPI (1.75x)"), 168});
        list.append({QStringLiteral("192 DPI (2.0x)"), 192});
        list.append({QStringLiteral("240 DPI (2.5x)"), 240});
        list.append({QStringLiteral("288 DPI (3.0x)"), 288});
        return list;
    }

    static void selectComboValue(QComboBox* combo, int value) {
        for (int i = 0; i < combo->count(); ++i) {
            if (combo->itemData(i).toInt() == value) {
                combo->setCurrentIndex(i);
                return;
            }
        }
        combo->addItem(QStringLiteral("%1").arg(value), value);
        combo->setCurrentIndex(combo->count() - 1);
    }

    void buildUi() {
        auto* root = new QVBoxLayout(this);
        root->setContentsMargins(14, 14, 14, 12);
        root->setSpacing(10);

        // Top Bar
        auto* topBar = new QHBoxLayout();
        topBar->setSpacing(10);
        auto* titleCol = new QVBoxLayout();
        titleCol->setSpacing(2);
        auto* titleLbl = new QLabel(QStringLiteral("System & Display Settings"), this);
        titleLbl->setObjectName(QStringLiteral("SettingsTitle"));
        auto* badgeLbl = new QLabel(QStringLiteral("Launcher & Settings are whitelisted at native DPI"), this);
        badgeLbl->setObjectName(QStringLiteral("WhitelistBadge"));
        badgeLbl->setWordWrap(true);
        titleCol->addWidget(titleLbl);
        titleCol->addWidget(badgeLbl);
        topBar->addLayout(titleCol, 1);

        auto* closeBtn = new QPushButton(QStringLiteral("Done"), this);
        closeBtn->setObjectName(QStringLiteral("SecondaryBtn"));
        closeBtn->setMinimumHeight(38);
        closeBtn->setCursor(Qt::PointingHandCursor);
        connect(closeBtn, &QPushButton::clicked, this, &QWidget::close);
        topBar->addWidget(closeBtn);
        root->addLayout(topBar);

        auto* tabs = new QTabWidget(this);
        tabs->setObjectName(QStringLiteral("SettingsTabs"));
        tabs->addTab(buildDisplayTab(tabs), QStringLiteral("Display && DPI"));
        tabs->addTab(buildVmTab(tabs), QStringLiteral("VM && Storage"));
        root->addWidget(tabs, 1);

        m_toast = new QLabel(this);
        m_toast->setObjectName(QStringLiteral("SettingsToast"));
        m_toast->setAlignment(Qt::AlignCenter);
        m_toast->setWordWrap(true);
        m_toast->hide();

        m_toastTimer = new QTimer(this);
        m_toastTimer->setSingleShot(true);
        connect(m_toastTimer, &QTimer::timeout, this, [this]() { m_toast->hide(); });

        setStyleSheet(QStringLiteral(R"(
            #SettingsRoot {
                background: #0f172a;
                color: #f8fafc;
            }
            #SettingsTitle {
                color: #ffffff;
                font-size: 18px;
                font-weight: 700;
            }
            #WhitelistBadge {
                color: #7dd3fc;
                font-size: 12px;
                font-weight: 500;
            }
            #SecondaryBtn {
                background: rgba(51, 65, 85, 0.85);
                border: 1px solid rgba(148, 163, 184, 0.30);
                border-radius: 12px;
                color: #f8fafc;
                padding: 0 14px;
                font-size: 13px;
                font-weight: 600;
            }
            #PrimaryBtn {
                background: #0284c7;
                border: 1px solid #38bdf8;
                border-radius: 14px;
                color: #ffffff;
                padding: 0 18px;
                font-size: 14px;
                font-weight: 600;
            }
            #PrimaryBtn:pressed {
                background: #0369a1;
            }
            QTabWidget::pane {
                border: 1px solid rgba(148, 163, 184, 0.20);
                border-radius: 16px;
                background: rgba(15, 23, 42, 0.90);
                padding: 6px;
            }
            QTabBar::tab {
                background: rgba(30, 41, 59, 0.72);
                color: #cbd5e1;
                padding: 9px 16px;
                margin-right: 6px;
                border-radius: 12px;
                font-size: 13px;
                font-weight: 600;
            }
            QTabBar::tab:selected {
                background: #0284c7;
                color: #ffffff;
            }
            #CardFrame {
                background: rgba(30, 41, 59, 0.78);
                border: 1px solid rgba(148, 163, 184, 0.20);
                border-radius: 14px;
            }
            #SectionTitle {
                color: #f1f5f9;
                font-size: 14px;
                font-weight: 700;
            }
            #HintLabel {
                color: #94a3b8;
                font-size: 12px;
            }
            QComboBox {
                background-color: #0f172a;
                border: 1px solid rgba(148, 163, 184, 0.35);
                border-radius: 10px;
                color: #f8fafc;
                padding: 6px 10px;
                min-height: 30px;
                font-size: 13px;
            }
            QComboBox::drop-down {
                border: none;
                width: 24px;
            }
            QLineEdit {
                background: rgba(15, 23, 42, 0.78);
                border: 1px solid rgba(148, 163, 184, 0.28);
                border-radius: 12px;
                color: #f8fafc;
                padding: 0 12px;
                font-size: 13px;
            }
            #SettingsToast {
                background: rgba(12, 74, 110, 0.96);
                border: 1px solid #38bdf8;
                border-radius: 14px;
                color: #f8fafc;
                padding: 10px 16px;
                font-size: 13px;
                font-weight: 600;
            }
            QScrollArea {
                background: transparent;
                border: none;
            }
            QScrollBar:vertical {
                background: transparent;
                width: 6px;
                margin: 2px 0px 2px 0px;
            }
            QScrollBar::handle:vertical {
                background: rgba(148, 163, 184, 0.45);
                border-radius: 3px;
                min-height: 28px;
            }
            QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical,
            QScrollBar::add-page:vertical, QScrollBar::sub-page:vertical {
                height: 0px;
                background: transparent;
            }
        )"));
    }

    QWidget* buildDisplayTab(QWidget* parent) {
        auto* tab = new QWidget(parent);
        auto* layout = new QVBoxLayout(tab);
        layout->setContentsMargins(8, 10, 8, 8);
        layout->setSpacing(10);

        auto* globalCard = new QFrame(tab);
        globalCard->setObjectName(QStringLiteral("CardFrame"));
        auto* globalLayout = new QVBoxLayout(globalCard);
        globalLayout->setContentsMargins(14, 12, 14, 12);
        globalLayout->setSpacing(8);

        auto* globalTitle = new QLabel(QStringLiteral("Global Guest DPI"), globalCard);
        globalTitle->setObjectName(QStringLiteral("SectionTitle"));
        auto* globalHint = new QLabel(
            QStringLiteral("Saved to ~/.config/podish/compositor.json and applied live (except whitelisted Launcher)."),
            globalCard);
        globalHint->setObjectName(QStringLiteral("HintLabel"));
        globalHint->setWordWrap(true);
        globalLayout->addWidget(globalTitle);
        globalLayout->addWidget(globalHint);

        auto* globalRow = new QHBoxLayout();
        m_globalDpiCombo = new QComboBox(globalCard);
        configureDarkComboBox(m_globalDpiCombo);
        for (const auto& pair : dpiChoices(false)) {
            m_globalDpiCombo->addItem(pair.first, pair.second);
        }
        connect(m_globalDpiCombo, qOverload<int>(&QComboBox::currentIndexChanged), this, [this](int) {
            if (m_loadingState)
                return;
            m_globalDpi = m_globalDpiCombo->currentData().toInt();
        });
        globalRow->addWidget(m_globalDpiCombo, 1);
        globalLayout->addLayout(globalRow);
        layout->addWidget(globalCard);

        auto* perAppHeader = new QHBoxLayout();
        auto* perAppTitle = new QLabel(QStringLiteral("Per-App Independent DPI (.desktop)"), tab);
        perAppTitle->setObjectName(QStringLiteral("SectionTitle"));
        perAppHeader->addWidget(perAppTitle, 1);
        layout->addLayout(perAppHeader);

        m_appFilter = new QLineEdit(tab);
        m_appFilter->setPlaceholderText(QStringLiteral("Filter .desktop apps..."));
        m_appFilter->setClearButtonEnabled(true);
        m_appFilter->setMinimumHeight(38);
        connect(m_appFilter, &QLineEdit::textChanged, this, [this]() { rebuildAppDpiList(); });
        layout->addWidget(m_appFilter);

        m_appScroll = new QScrollArea(tab);
        m_appScroll->setWidgetResizable(true);
        m_appScroll->setHorizontalScrollBarPolicy(Qt::ScrollBarAlwaysOff);
        QScroller::grabGesture(m_appScroll->viewport(), QScroller::LeftMouseButtonGesture);

        m_appListContainer = new QWidget(m_appScroll);
        m_appListContainer->setStyleSheet(QStringLiteral("background: transparent;"));
        m_appListLayout = new QVBoxLayout(m_appListContainer);
        m_appListLayout->setContentsMargins(0, 0, 8, 8);
        m_appListLayout->setSpacing(8);
        m_appListLayout->setAlignment(Qt::AlignTop);
        m_appScroll->setWidget(m_appListContainer);
        layout->addWidget(m_appScroll, 1);

        auto* applyDisplayBtn = new QPushButton(QStringLiteral("Save && Hot-Reload Display"), tab);
        applyDisplayBtn->setObjectName(QStringLiteral("PrimaryBtn"));
        applyDisplayBtn->setMinimumHeight(44);
        applyDisplayBtn->setCursor(Qt::PointingHandCursor);
        connect(applyDisplayBtn, &QPushButton::clicked, this, &SettingsWindow::saveAndApplyDisplayConfig);
        layout->addWidget(applyDisplayBtn);

        return tab;
    }

    QWidget* buildVmTab(QWidget* parent) {
        auto* tab = new QWidget(parent);
        auto* scroll = new QScrollArea(tab);
        scroll->setWidgetResizable(true);
        scroll->setHorizontalScrollBarPolicy(Qt::ScrollBarAlwaysOff);
        QScroller::grabGesture(scroll->viewport(), QScroller::LeftMouseButtonGesture);

        auto* inner = new QWidget(scroll);
        inner->setStyleSheet(QStringLiteral("background: transparent;"));
        auto* layout = new QVBoxLayout(inner);
        layout->setContentsMargins(8, 10, 8, 10);
        layout->setSpacing(12);
        layout->setAlignment(Qt::AlignTop);

        auto* infoCard = new QFrame(inner);
        infoCard->setObjectName(QStringLiteral("CardFrame"));
        auto* infoLayout = new QVBoxLayout(infoCard);
        infoLayout->setContentsMargins(14, 12, 14, 12);
        infoLayout->setSpacing(6);
        auto* infoTitle = new QLabel(QStringLiteral("Host Runtime Preferences"), infoCard);
        infoTitle->setObjectName(QStringLiteral("SectionTitle"));
        auto* infoHint =
            new QLabel(QStringLiteral("VM settings are sent via the Compositor bridge and stored in Host UserDefaults "
                                      "(outside SilkFS). Changes take effect on next launch."),
                       infoCard);
        infoHint->setObjectName(QStringLiteral("HintLabel"));
        infoHint->setWordWrap(true);
        m_vmActiveLabel = new QLabel(QStringLiteral("Current active VM configuration: loading..."), infoCard);
        m_vmActiveLabel->setObjectName(QStringLiteral("WhitelistBadge"));
        m_vmActiveLabel->setWordWrap(true);
        infoLayout->addWidget(infoTitle);
        infoLayout->addWidget(infoHint);
        infoLayout->addWidget(m_vmActiveLabel);
        layout->addWidget(infoCard);

        auto* memCard = new QFrame(inner);
        memCard->setObjectName(QStringLiteral("CardFrame"));
        auto* memLayout = new QVBoxLayout(memCard);
        memLayout->setContentsMargins(14, 12, 14, 12);
        memLayout->setSpacing(8);
        auto* memTitle = new QLabel(QStringLiteral("Guest Memory Quota"), memCard);
        memTitle->setObjectName(QStringLiteral("SectionTitle"));
        m_memoryCombo = new QComboBox(memCard);
        configureDarkComboBox(m_memoryCombo);
        m_memoryCombo->addItem(QStringLiteral("Default / Unlimited"), 0);
        m_memoryCombo->addItem(QStringLiteral("256 MB"), 256);
        m_memoryCombo->addItem(QStringLiteral("512 MB"), 512);
        m_memoryCombo->addItem(QStringLiteral("1024 MB (1 GB)"), 1024);
        m_memoryCombo->addItem(QStringLiteral("1536 MB"), 1536);
        m_memoryCombo->addItem(QStringLiteral("2048 MB (2 GB)"), 2048);
        m_memoryCombo->addItem(QStringLiteral("3072 MB (3 GB)"), 3072);
        m_memoryCombo->addItem(QStringLiteral("4096 MB (4 GB)"), 4096);
        memLayout->addWidget(memTitle);
        memLayout->addWidget(m_memoryCombo);
        layout->addWidget(memCard);

        auto* cacheCard = new QFrame(inner);
        cacheCard->setObjectName(QStringLiteral("CardFrame"));
        auto* cacheLayout = new QVBoxLayout(cacheCard);
        cacheLayout->setContentsMargins(14, 12, 14, 12);
        cacheLayout->setSpacing(8);
        auto* cacheTitle = new QLabel(QStringLiteral("CPU Block Cache Limit"), cacheCard);
        cacheTitle->setObjectName(QStringLiteral("SectionTitle"));
        m_blockCacheCombo = new QComboBox(cacheCard);
        configureDarkComboBox(m_blockCacheCombo);
        m_blockCacheCombo->addItem(QStringLiteral("Engine Default"), 0);
        m_blockCacheCombo->addItem(QStringLiteral("16 MB"), 16);
        m_blockCacheCombo->addItem(QStringLiteral("32 MB"), 32);
        m_blockCacheCombo->addItem(QStringLiteral("64 MB"), 64);
        m_blockCacheCombo->addItem(QStringLiteral("128 MB"), 128);
        m_blockCacheCombo->addItem(QStringLiteral("256 MB"), 256);
        m_blockCacheCombo->addItem(QStringLiteral("512 MB"), 512);
        cacheLayout->addWidget(cacheTitle);
        cacheLayout->addWidget(m_blockCacheCombo);
        layout->addWidget(cacheCard);

        scroll->setWidget(inner);

        auto* outer = new QVBoxLayout(tab);
        outer->setContentsMargins(0, 0, 0, 8);
        outer->setSpacing(10);
        outer->addWidget(scroll, 1);

        auto* saveVmBtn = new QPushButton(QStringLiteral("Save VM Settings to Host"), tab);
        saveVmBtn->setObjectName(QStringLiteral("PrimaryBtn"));
        saveVmBtn->setMinimumHeight(44);
        saveVmBtn->setCursor(Qt::PointingHandCursor);
        connect(saveVmBtn, &QPushButton::clicked, this, &SettingsWindow::saveVmSettingsToHost);
        outer->addWidget(saveVmBtn);

        return tab;
    }

    void loadState() {
        m_loadingState = true;
        m_globalDpi = 0;
        m_appDpis.clear();

        QFile cfgFile(compositorConfigPath());
        if (cfgFile.open(QIODevice::ReadOnly)) {
            const QJsonDocument doc = QJsonDocument::fromJson(cfgFile.readAll());
            if (doc.isObject()) {
                const QJsonObject root = doc.object();
                m_globalDpi = root.value(QStringLiteral("globalDpi")).toInt(0);
                const QJsonObject appsObj = root.value(QStringLiteral("appDpis")).toObject();
                for (auto it = appsObj.begin(); it != appsObj.end(); ++it) {
                    const int v = it.value().toInt(0);
                    if (v > 0) {
                        m_appDpis.insert(it.key().toLower(), v);
                    }
                }
            }
        }
        selectComboValue(m_globalDpiCombo, m_globalDpi);
        m_loadingState = false;
        rebuildAppDpiList();

        QJsonObject vmReply;
        if (sendCompositorControl(QJsonObject{{QStringLiteral("cmd"), QStringLiteral("get_vm_settings")}}, &vmReply)) {
            const int savedMem = vmReply.value(QStringLiteral("memoryLimitMb")).toInt(0);
            const int savedCache = vmReply.value(QStringLiteral("blockCacheMb")).toInt(0);
            const int activeMem = vmReply.value(QStringLiteral("activeMemoryLimitMb")).toInt(0);
            const int activeCache = vmReply.value(QStringLiteral("activeBlockCacheMb")).toInt(0);
            selectComboValue(m_memoryCombo, savedMem);
            selectComboValue(m_blockCacheCombo, savedCache);
            updateVmActiveLabel(activeMem, activeCache, savedMem, savedCache);
        } else {
            m_vmActiveLabel->setText(QStringLiteral("Compositor control bridge unavailable"));
        }
    }

    void updateVmActiveLabel(int activeMem, int activeCache, int savedMem, int savedCache) {
        const QString memStr = activeMem > 0 ? QStringLiteral("%1 MB").arg(activeMem) : QStringLiteral("Default");
        const QString cacheStr = activeCache > 0 ? QStringLiteral("%1 MB").arg(activeCache) : QStringLiteral("Default");
        QString text = QStringLiteral("Active this session: Memory %1 • Block Cache %2").arg(memStr, cacheStr);
        if (savedMem != activeMem || savedCache != activeCache) {
            text += QStringLiteral("\nPending restart: Memory %1 • Block Cache %2")
                        .arg(savedMem > 0 ? QStringLiteral("%1 MB").arg(savedMem) : QStringLiteral("Default"),
                             savedCache > 0 ? QStringLiteral("%1 MB").arg(savedCache) : QStringLiteral("Default"));
        }
        m_vmActiveLabel->setText(text);
    }

    void rebuildAppDpiList() {
        while (QLayoutItem* item = m_appListLayout->takeAt(0)) {
            if (QWidget* w = item->widget())
                w->deleteLater();
            delete item;
        }

        const QString filter = m_appFilter ? m_appFilter->text().trimmed() : QString();
        for (const AppEntry& app : m_apps) {
            if (app.id.compare(QStringLiteral("podish-launcher"), Qt::CaseInsensitive) == 0)
                continue;
            if (!filter.isEmpty() && !app.name.contains(filter, Qt::CaseInsensitive) &&
                !app.id.contains(filter, Qt::CaseInsensitive)) {
                continue;
            }

            auto* row = new QFrame(m_appListContainer);
            row->setObjectName(QStringLiteral("CardFrame"));
            auto* rowLayout = new QHBoxLayout(row);
            rowLayout->setContentsMargins(10, 8, 10, 8);
            rowLayout->setSpacing(10);

            auto* iconLbl = new QLabel(row);
            iconLbl->setPixmap(resolveAppIcon(app.iconName, app.name).pixmap(30, 30));
            iconLbl->setFixedSize(32, 32);
            rowLayout->addWidget(iconLbl);

            auto* nameCol = new QVBoxLayout();
            nameCol->setSpacing(1);
            auto* appNameLbl = new QLabel(app.name, row);
            appNameLbl->setStyleSheet(QStringLiteral("color: #f8fafc; font-size: 13px; font-weight: 600;"));
            auto* appIdLbl = new QLabel(app.id + QStringLiteral(".desktop"), row);
            appIdLbl->setStyleSheet(QStringLiteral("color: #94a3b8; font-size: 11px;"));
            nameCol->addWidget(appNameLbl);
            nameCol->addWidget(appIdLbl);
            rowLayout->addLayout(nameCol, 1);

            auto* combo = new QComboBox(row);
            configureDarkComboBox(combo);
            combo->setMinimumWidth(118);
            combo->setMaximumWidth(140);
            for (const auto& pair : dpiChoices(true)) {
                combo->addItem(pair.first, pair.second);
            }
            const int curVal = m_appDpis.value(app.id.toLower(), 0);
            selectComboValue(combo, curVal);
            const QString appKey = app.id.toLower();
            connect(combo, qOverload<int>(&QComboBox::currentIndexChanged), this, [this, combo, appKey](int) {
                const int val = combo->currentData().toInt();
                if (val <= 0)
                    m_appDpis.remove(appKey);
                else
                    m_appDpis.insert(appKey, val);
            });
            rowLayout->addWidget(combo);
            m_appListLayout->addWidget(row);
        }
    }

    void saveAndApplyDisplayConfig() {
        const QString path = compositorConfigPath();
        QFileInfo fi(path);
        QDir().mkpath(fi.absolutePath());

        QJsonObject appsObj;
        for (auto it = m_appDpis.constBegin(); it != m_appDpis.constEnd(); ++it) {
            if (it.value() > 0) {
                appsObj.insert(it.key(), it.value());
            }
        }
        QJsonObject root;
        root.insert(QStringLiteral("globalDpi"), m_globalDpi);
        root.insert(QStringLiteral("appDpis"), appsObj);

        QSaveFile file(path);
        if (!file.open(QIODevice::WriteOnly | QIODevice::Truncate)) {
            showToast(QStringLiteral("Failed to write ~/.config/podish/compositor.json"));
            return;
        }
        file.write(QJsonDocument(root).toJson(QJsonDocument::Indented));
        if (!file.commit()) {
            showToast(QStringLiteral("Failed to commit display settings"));
            return;
        }

        QJsonObject reply;
        if (sendCompositorControl(QJsonObject{{QStringLiteral("cmd"), QStringLiteral("reload_config")}}, &reply)) {
            showToast(QStringLiteral("Display settings saved and hot-reloaded"));
        } else {
            showToast(QStringLiteral("Saved to ~/.config/podish/compositor.json (control socket unreachable)"));
        }
    }

    void saveVmSettingsToHost() {
        const int memMb = m_memoryCombo->currentData().toInt();
        const int cacheMb = m_blockCacheCombo->currentData().toInt();
        QJsonObject reply;
        const QJsonObject req{
            {QStringLiteral("cmd"), QStringLiteral("set_vm_settings")},
            {QStringLiteral("memoryLimitMb"), memMb},
            {QStringLiteral("blockCacheMb"), cacheMb},
        };
        if (sendCompositorControl(req, &reply)) {
            const bool requiresRestart = reply.value(QStringLiteral("requiresRestart")).toBool(true);
            QJsonObject statusReply;
            if (sendCompositorControl(QJsonObject{{QStringLiteral("cmd"), QStringLiteral("get_vm_settings")}},
                                      &statusReply)) {
                updateVmActiveLabel(statusReply.value(QStringLiteral("activeMemoryLimitMb")).toInt(0),
                                    statusReply.value(QStringLiteral("activeBlockCacheMb")).toInt(0), memMb, cacheMb);
            }
            if (requiresRestart) {
                showToast(QStringLiteral("Saved to Host UserDefaults — takes effect on next launch"));
            } else {
                showToast(QStringLiteral("VM settings saved (matches active configuration)"));
            }
        } else {
            showToast(QStringLiteral("Could not reach Compositor Host bridge"));
        }
    }

    void showToast(const QString& text) {
        m_toast->setText(text);
        m_toast->adjustSize();
        positionToast();
        m_toast->show();
        m_toast->raise();
        m_toastTimer->start(2800);
    }

    void positionToast() {
        if (!m_toast)
            return;
        const int maxW = std::max(220, width() - 32);
        m_toast->setMaximumWidth(maxW);
        m_toast->adjustSize();
        const int x = (width() - m_toast->width()) / 2;
        const int y = std::max(16, height() - m_toast->height() - 62);
        m_toast->move(x, y);
    }

    QList<AppEntry> m_apps;
    bool m_mobileMode = false;
    bool m_loadingState = false;
    int m_globalDpi = 0;
    QMap<QString, int> m_appDpis;

    QComboBox* m_globalDpiCombo = nullptr;
    QLineEdit* m_appFilter = nullptr;
    QScrollArea* m_appScroll = nullptr;
    QWidget* m_appListContainer = nullptr;
    QVBoxLayout* m_appListLayout = nullptr;

    QLabel* m_vmActiveLabel = nullptr;
    QComboBox* m_memoryCombo = nullptr;
    QComboBox* m_blockCacheCombo = nullptr;

    QLabel* m_toast = nullptr;
    QTimer* m_toastTimer = nullptr;
};

// ============================================================================
// QOpenGLWindow using the active Wayland EGL context.
// ============================================================================

struct GlTextureEntry {
    GLuint id = 0;
    int pxW = 0;
    int pxH = 0;
    QString key;
};

struct LauncherGlApi {
    void* lib = nullptr;
    const GLubyte* (*getString)(GLenum) = nullptr;
    GLuint (*createShader)(GLenum) = nullptr;
    void (*shaderSource)(GLuint, GLsizei, const GLchar* const*, const GLint*) = nullptr;
    void (*compileShader)(GLuint) = nullptr;
    void (*getShaderiv)(GLuint, GLenum, GLint*) = nullptr;
    void (*getShaderInfoLog)(GLuint, GLsizei, GLsizei*, GLchar*) = nullptr;
    void (*deleteShader)(GLuint) = nullptr;
    GLuint (*createProgram)() = nullptr;
    void (*attachShader)(GLuint, GLuint) = nullptr;
    void (*bindAttribLocation)(GLuint, GLuint, const GLchar*) = nullptr;
    void (*linkProgram)(GLuint) = nullptr;
    void (*getProgramiv)(GLuint, GLenum, GLint*) = nullptr;
    void (*getProgramInfoLog)(GLuint, GLsizei, GLsizei*, GLchar*) = nullptr;
    void (*deleteProgram)(GLuint) = nullptr;
    void (*useProgram)(GLuint) = nullptr;
    GLint (*getUniformLocation)(GLuint, const GLchar*) = nullptr;
    void (*uniform1i)(GLint, GLint) = nullptr;
    void (*uniform2f)(GLint, GLfloat, GLfloat) = nullptr;
    void (*uniform4f)(GLint, GLfloat, GLfloat, GLfloat, GLfloat) = nullptr;
    void (*genBuffers)(GLsizei, GLuint*) = nullptr;
    void (*deleteBuffers)(GLsizei, const GLuint*) = nullptr;
    void (*bindBuffer)(GLenum, GLuint) = nullptr;
    void (*bufferData)(GLenum, GLsizeiptr, const void*, GLenum) = nullptr;
    void (*enableVertexAttribArray)(GLuint) = nullptr;
    void (*disableVertexAttribArray)(GLuint) = nullptr;
    void (*vertexAttribPointer)(GLuint, GLint, GLenum, GLboolean, GLsizei, const void*) = nullptr;
    void (*genTextures)(GLsizei, GLuint*) = nullptr;
    void (*deleteTextures)(GLsizei, const GLuint*) = nullptr;
    void (*activeTexture)(GLenum) = nullptr;
    void (*bindTexture)(GLenum, GLuint) = nullptr;
    void (*texParameteri)(GLenum, GLenum, GLint) = nullptr;
    void (*pixelStorei)(GLenum, GLint) = nullptr;
    void (*texImage2D)(GLenum, GLint, GLint, GLsizei, GLsizei, GLint, GLenum, GLenum, const void*) = nullptr;
    void (*viewport)(GLint, GLint, GLsizei, GLsizei) = nullptr;
    void (*enable)(GLenum) = nullptr;
    void (*disable)(GLenum) = nullptr;
    void (*blendFunc)(GLenum, GLenum) = nullptr;
    void (*scissor)(GLint, GLint, GLsizei, GLsizei) = nullptr;
    void (*drawArrays)(GLenum, GLint, GLsizei) = nullptr;

    void* resolve(QOpenGLContext* ctx, const char* name) {
        if (lib) {
            if (void* sym = ::dlsym(lib, name))
                return sym;
        }
        if (ctx) {
            if (auto fn = ctx->getProcAddress(name))
                return reinterpret_cast<void*>(fn);
        }
        return nullptr;
    }

    bool load(QOpenGLContext* ctx) {
#ifndef PODISH_LAUNCHER_VULKAN
        // The Podish GLES shim exports these directly.
        if (!lib && ctx->isOpenGLES()) {
            lib = ::dlopen("/run/podish-gles/libGLESv2.so.2", RTLD_NOW | RTLD_LOCAL);
            if (!lib)
                lib = ::dlopen("libGLESv2.so.2", RTLD_NOW | RTLD_LOCAL);
        }
#endif
#define LOAD_GL(field, sym)                                                                                            \
    field = reinterpret_cast<decltype(field)>(resolve(ctx, sym));                                                      \
    if (!field) {                                                                                                      \
        std::fprintf(stderr, "[podish-launcher] Missing GL symbol: %s\n", sym);                                        \
        return false;                                                                                                  \
    }
        LOAD_GL(getString, "glGetString");
        LOAD_GL(createShader, "glCreateShader");
        LOAD_GL(shaderSource, "glShaderSource");
        LOAD_GL(compileShader, "glCompileShader");
        LOAD_GL(getShaderiv, "glGetShaderiv");
        LOAD_GL(getShaderInfoLog, "glGetShaderInfoLog");
        LOAD_GL(deleteShader, "glDeleteShader");
        LOAD_GL(createProgram, "glCreateProgram");
        LOAD_GL(attachShader, "glAttachShader");
        LOAD_GL(bindAttribLocation, "glBindAttribLocation");
        LOAD_GL(linkProgram, "glLinkProgram");
        LOAD_GL(getProgramiv, "glGetProgramiv");
        LOAD_GL(getProgramInfoLog, "glGetProgramInfoLog");
        LOAD_GL(deleteProgram, "glDeleteProgram");
        LOAD_GL(useProgram, "glUseProgram");
        LOAD_GL(getUniformLocation, "glGetUniformLocation");
        LOAD_GL(uniform1i, "glUniform1i");
        LOAD_GL(uniform2f, "glUniform2f");
        LOAD_GL(uniform4f, "glUniform4f");
        LOAD_GL(genBuffers, "glGenBuffers");
        LOAD_GL(deleteBuffers, "glDeleteBuffers");
        LOAD_GL(bindBuffer, "glBindBuffer");
        LOAD_GL(bufferData, "glBufferData");
        LOAD_GL(enableVertexAttribArray, "glEnableVertexAttribArray");
        LOAD_GL(disableVertexAttribArray, "glDisableVertexAttribArray");
        LOAD_GL(vertexAttribPointer, "glVertexAttribPointer");
        LOAD_GL(genTextures, "glGenTextures");
        LOAD_GL(deleteTextures, "glDeleteTextures");
        LOAD_GL(activeTexture, "glActiveTexture");
        LOAD_GL(bindTexture, "glBindTexture");
        LOAD_GL(texParameteri, "glTexParameteri");
        LOAD_GL(pixelStorei, "glPixelStorei");
        LOAD_GL(texImage2D, "glTexImage2D");
        LOAD_GL(viewport, "glViewport");
        LOAD_GL(enable, "glEnable");
        LOAD_GL(disable, "glDisable");
        LOAD_GL(blendFunc, "glBlendFunc");
        LOAD_GL(scissor, "glScissor");
        LOAD_GL(drawArrays, "glDrawArrays");
#undef LOAD_GL
        return true;
    }

    GLuint compileShaderSource(GLenum type, const char* preamble, const char* source) {
        GLuint sh = createShader(type);
        if (!sh)
            return 0;
        const char* parts[] = {preamble, source};
        shaderSource(sh, 2, parts, nullptr);
        compileShader(sh);
        GLint ok = 0;
        getShaderiv(sh, GL_COMPILE_STATUS, &ok);
        if (!ok) {
            char log[1024] = {};
            GLsizei len = 0;
            getShaderInfoLog(sh, sizeof(log) - 1, &len, log);
            std::fprintf(stderr, "[podish-launcher] GL shader compile error: %s\n", log);
            deleteShader(sh);
            return 0;
        }
        return sh;
    }

    GLuint buildProgram(bool gles, const char* vertSrc, const char* fragSrc) {
        const char* vertPreamble = gles ? "#version 300 es\nprecision highp float;\n"
                                          "#define VERTEX_ATTRIBUTE in\n#define VARYING out\n"
                                        : "#version 120\n#define VERTEX_ATTRIBUTE attribute\n"
                                          "#define VARYING varying\n";
        const char* fragPreamble = gles ? "#version 300 es\nprecision highp float;\n"
                                          "#define VARYING in\n#define SAMPLE_TEXTURE texture\n"
                                          "out vec4 fragColor;\n"
                                        : "#version 120\n#define VARYING varying\n"
                                          "#define SAMPLE_TEXTURE texture2D\n#define fragColor gl_FragColor\n";
        GLuint vs = compileShaderSource(GL_VERTEX_SHADER, vertPreamble, vertSrc);
        if (!vs)
            return 0;
        GLuint fs = compileShaderSource(GL_FRAGMENT_SHADER, fragPreamble, fragSrc);
        if (!fs) {
            deleteShader(vs);
            return 0;
        }
        GLuint prog = createProgram();
        attachShader(prog, vs);
        attachShader(prog, fs);
        bindAttribLocation(prog, 0, "aUv");
        linkProgram(prog);
        deleteShader(vs);
        deleteShader(fs);
        GLint ok = 0;
        getProgramiv(prog, GL_LINK_STATUS, &ok);
        if (!ok) {
            char log[1024] = {};
            GLsizei len = 0;
            getProgramInfoLog(prog, sizeof(log) - 1, &len, log);
            std::fprintf(stderr, "[podish-launcher] GL program link error: %s\n", log);
            deleteProgram(prog);
            return 0;
        }
        return prog;
    }
};

class GlLauncherWindow : public QOpenGLWindow {
public:
    explicit GlLauncherWindow(bool mobileMode)
        : QOpenGLWindow(QOpenGLWindow::NoPartialUpdate)
        , m_mobileMode(mobileMode) {
        setTitle(QStringLiteral("Podish Desktop"));
        setMinimumSize(QSize(320, 420));

        m_categories = {
            QStringLiteral("All"),       QStringLiteral("Favorites"), QStringLiteral("System"),
            QStringLiteral("Utilities"), QStringLiteral("Graphics"),  QStringLiteral("Development"),
            QStringLiteral("Settings"),
        };

        reloadApps();
        updateStatus();

        m_clockTimer = new QTimer(this);
        connect(m_clockTimer, &QTimer::timeout, this, [this]() {
            updateStatus();
            requestRender();
        });
        m_clockTimer->start(10000);

        m_toastTimer = new QTimer(this);
        m_toastTimer->setSingleShot(true);
        connect(m_toastTimer, &QTimer::timeout, this, [this]() {
            m_toastText.clear();
            requestRender();
        });

        m_animTimer = new QTimer(this);
        m_animTimer->setInterval(16);
        connect(m_animTimer, &QTimer::timeout, this, &GlLauncherWindow::stepPhysics);

        m_cursorBlinkTimer = new QTimer(this);
        m_cursorBlinkTimer->setInterval(530);
        connect(m_cursorBlinkTimer, &QTimer::timeout, this, [this]() {
            if (!m_searchFocused) {
                m_cursorBlinkTimer->stop();
                return;
            }
            m_cursorOn = !m_cursorOn;
            requestRender();
        });
    }

    ~GlLauncherWindow() override {
        if (context() && m_glReady) {
            makeCurrent();
            destroyAllTextures();
            if (m_quadVbo != 0) {
                m_gl.deleteBuffers(1, &m_quadVbo);
                m_quadVbo = 0;
            }
            if (m_program != 0) {
                m_gl.deleteProgram(m_program);
                m_program = 0;
            }
            doneCurrent();
        }
    }

protected:
    void initializeGL() override {
        if (!m_gl.load(context())) {
            std::fprintf(stderr, "[podish-launcher] Failed to resolve GL functions\n");
            std::fflush(stderr);
            return;
        }
        const char* vendor = reinterpret_cast<const char*>(m_gl.getString(GL_VENDOR));
        const char* renderer = reinterpret_cast<const char*>(m_gl.getString(GL_RENDERER));
        const char* version = reinterpret_cast<const char*>(m_gl.getString(GL_VERSION));
        std::fprintf(stderr, "[podish-launcher] GPU Accelerated GL: vendor=%s renderer=%s version=%s\n",
                     vendor ? vendor : "?", renderer ? renderer : "?", version ? version : "?");
        std::fflush(stderr);

        static const char* kVertSrc = R"(
VERTEX_ATTRIBUTE vec2 aUv;
uniform vec2 uViewport;
uniform vec4 uRect;
VARYING vec2 vUv;
VARYING vec2 vLocal;
VARYING vec2 vSize;
void main() {
    vUv = aUv;
    vSize = uRect.zw;
    vLocal = aUv * vSize;
    vec2 pos = (uRect.xy + vLocal) / max(uViewport, vec2(1.0));
    gl_Position = vec4(pos.x * 2.0 - 1.0, 1.0 - pos.y * 2.0, 0.0, 1.0);
}
)";

        static const char* kFragSrc = R"(
uniform int uMode;
uniform vec4 uColor;
uniform vec4 uBorderColor;
uniform vec4 uParams; // x: radiusPx, y: borderPx, z: alpha, w: unused
uniform sampler2D uTex;
VARYING vec2 vUv;
VARYING vec2 vLocal;
VARYING vec2 vSize;

vec3 plasmaWallpaper() {
    vec2 p = vUv - vec2(0.5);
    float aspect = vSize.x / max(vSize.y, 1.0);
    vec2 q = vec2(p.x * aspect, p.y);

    vec3 topCol = vec3(0.055, 0.095, 0.215);
    vec3 midCol = vec3(0.065, 0.175, 0.295);
    vec3 botCol = vec3(0.115, 0.085, 0.245);
    vec3 col = mix(topCol, midCol, smoothstep(0.0, 0.55, vUv.y));
    col = mix(col, botCol, smoothstep(0.45, 1.0, vUv.y));

    float cyanGlow = exp(-2.4 * dot(q - vec2(-0.22 * aspect, -0.18), q - vec2(-0.22 * aspect, -0.18)));
    float tealGlow = exp(-2.8 * dot(q - vec2(0.28 * aspect, 0.05), q - vec2(0.28 * aspect, 0.05)));
    float magentaGlow = exp(-2.6 * dot(q - vec2(0.18 * aspect, 0.36), q - vec2(0.18 * aspect, 0.36)));
    col += cyanGlow * vec3(0.06, 0.24, 0.34);
    col += tealGlow * vec3(0.04, 0.22, 0.28);
    col += magentaGlow * vec3(0.24, 0.08, 0.26);

    float wave1 = vUv.y - (0.58 + 0.075 * sin(vUv.x * 3.1 + 0.4) - 0.06 * vUv.x);
    float layer1 = smoothstep(-0.004, 0.004, wave1);
    vec3 ridge1 = mix(vec3(0.08, 0.26, 0.38), vec3(0.07, 0.14, 0.26), smoothstep(0.0, 0.35, wave1));
    col = mix(col, ridge1, layer1 * 0.72);
    col += exp(-55.0 * abs(wave1)) * vec3(0.18, 0.52, 0.68) * 0.55;

    float wave2 = vUv.y - (0.72 + 0.065 * sin(vUv.x * 2.6 + 2.3) + 0.05 * vUv.x);
    float layer2 = smoothstep(-0.004, 0.004, wave2);
    vec3 ridge2 = mix(vec3(0.14, 0.13, 0.32), vec3(0.06, 0.08, 0.18), smoothstep(0.0, 0.30, wave2));
    col = mix(col, ridge2, layer2 * 0.85);
    col += exp(-50.0 * abs(wave2)) * vec3(0.45, 0.28, 0.62) * 0.50;

    float ribbon = (vUv.y - 0.32) + 0.22 * (vUv.x - 0.5) + 0.05 * sin(vUv.x * 4.2);
    col += exp(-12.0 * abs(ribbon)) * vec3(0.09, 0.28, 0.40) * 0.50;
    col += exp(-75.0 * abs(ribbon)) * vec3(0.22, 0.58, 0.76) * 0.38;

    float grain = fract(52.9829189 * fract(dot(floor(gl_FragCoord.xy), vec2(0.06711056, 0.00583715))));
    col = col * (1.0 - 0.18 * smoothstep(0.35, 1.05, length(p))) + (grain - 0.5) / 255.0;
    // Soft Plasma Mobile scrim overlay
    float scrimA = mix(0.14, 0.30, smoothstep(0.35, 1.0, vUv.y));
    return mix(col, vec3(0.028, 0.048, 0.105), scrimA);
}

float sdRoundedBox(vec2 p, vec2 b, float r) {
    vec2 q = abs(p) - b + vec2(r);
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

void main() {
    if (uMode == 0) {
        fragColor = vec4(plasmaWallpaper(), 1.0);
        return;
    }
    if (uMode == 2) {
        vec4 s = SAMPLE_TEXTURE(uTex, vUv);
        fragColor = s * uParams.z;
        return;
    }
    // uMode == 1: Anti-aliased rounded glass panel with optional border (premultiplied output)
    float r = min(uParams.x, min(vSize.x, vSize.y) * 0.5);
    float border = uParams.y;
    float d = sdRoundedBox(vLocal - vSize * 0.5, vSize * 0.5, r);
    float aa = max(0.75 * fwidth(d), 0.5);
    float outerCov = 1.0 - smoothstep(-aa, aa, d);
    if (outerCov <= 0.001) {
        discard;
    }
    vec4 fillPm = vec4(uColor.rgb * uColor.a, uColor.a);
    if (border > 0.01 && uBorderColor.a > 0.001) {
        float innerCov = 1.0 - smoothstep(-aa, aa, d + border);
        float borderCov = clamp(outerCov - innerCov, 0.0, 1.0);
        vec4 borderPm = vec4(uBorderColor.rgb * uBorderColor.a, uBorderColor.a);
        // Composite border over fill inside the shape
        vec4 fillPart = fillPm * innerCov;
        vec4 borderPart = borderPm * borderCov + fillPm * (borderCov * (1.0 - borderPm.a));
        fragColor = fillPart + borderPart;
    } else {
        fragColor = fillPm * outerCov;
    }
}
)";

        m_program = m_gl.buildProgram(context()->isOpenGLES(), kVertSrc, kFragSrc);
        if (m_program == 0) {
            std::fflush(stderr);
            return;
        }

        m_uViewport = m_gl.getUniformLocation(m_program, "uViewport");
        m_uRect = m_gl.getUniformLocation(m_program, "uRect");
        m_uMode = m_gl.getUniformLocation(m_program, "uMode");
        m_uColor = m_gl.getUniformLocation(m_program, "uColor");
        m_uBorderColor = m_gl.getUniformLocation(m_program, "uBorderColor");
        m_uParams = m_gl.getUniformLocation(m_program, "uParams");
        m_uTex = m_gl.getUniformLocation(m_program, "uTex");

        static const GLfloat kQuad[8] = {0.f, 0.f, 1.f, 0.f, 0.f, 1.f, 1.f, 1.f};
        m_gl.genBuffers(1, &m_quadVbo);
        m_gl.bindBuffer(GL_ARRAY_BUFFER, m_quadVbo);
        m_gl.bufferData(GL_ARRAY_BUFFER, sizeof(kQuad), kQuad, GL_STATIC_DRAW);
        m_gl.bindBuffer(GL_ARRAY_BUFFER, 0);

        m_glReady = true;
    }

    void resizeGL(int w, int h) override {
        Q_UNUSED(w);
        Q_UNUSED(h);
        recomputeLayout();
    }

    void paintGL() override {
        if (!m_glReady || m_program == 0)
            return;

        const qreal dpr = std::max<qreal>(1.0, devicePixelRatio());
        const int fbW = std::max(1, static_cast<int>(std::lround(width() * dpr)));
        const int fbH = std::max(1, static_cast<int>(std::lround(height() * dpr)));
        m_lastDpr = dpr;

        recomputeLayout();

        m_gl.disable(GL_DEPTH_TEST);
        m_gl.disable(GL_SCISSOR_TEST);
        m_gl.enable(GL_BLEND);
        m_gl.blendFunc(GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
        m_gl.viewport(0, 0, fbW, fbH);

        m_gl.useProgram(m_program);
        m_gl.uniform2f(m_uViewport, static_cast<float>(fbW), static_cast<float>(fbH));
        m_gl.uniform1i(m_uTex, 0);

        m_gl.bindBuffer(GL_ARRAY_BUFFER, m_quadVbo);
        m_gl.enableVertexAttribArray(0);
        m_gl.vertexAttribPointer(0, 2, GL_FLOAT, GL_FALSE, 0, nullptr);

        // 1. Plasma Mobile Aurora Wallpaper (100% GPU shader)
        drawWallpaper(fbW, fbH);

        // 2. Hero Clock + Status Pill
        {
            const QString clockKey = QStringLiteral("clock:%1:%2x%3@%4")
                                         .arg(m_clockText)
                                         .arg(static_cast<int>(m_clockRect.width()))
                                         .arg(static_cast<int>(m_clockRect.height()))
                                         .arg(dpr, 0, 'f', 2);
            const auto& clockTex =
                ensureTexture("clock", clockKey, m_clockRect.size(), dpr, [&](QPainter& p, const QSizeF& sz) {
                    QFont f = QApplication::font();
                    f.setPixelSize(m_compactLayout ? 44 : 48);
                    f.setWeight(QFont::Light);
                    f.setLetterSpacing(QFont::AbsoluteSpacing, 1.0);
                    p.setFont(f);
                    p.setPen(Qt::white);
                    p.drawText(QRectF(0, 0, sz.width(), sz.height()), Qt::AlignCenter, m_clockText);
                });
            drawTexture(m_clockRect, clockTex, dpr);

            drawGlassRect(m_statusPillRect, 14.0, QColor(12, 20, 38, 118), QColor(255, 255, 255, 42), 1.0, dpr);
            const QString metaKey = QStringLiteral("meta:%1:%2x%3@%4")
                                        .arg(m_metaText)
                                        .arg(static_cast<int>(m_statusPillRect.width()))
                                        .arg(static_cast<int>(m_statusPillRect.height()))
                                        .arg(dpr, 0, 'f', 2);
            const auto& metaTex =
                ensureTexture("meta", metaKey, m_statusPillRect.size(), dpr, [&](QPainter& p, const QSizeF& sz) {
                    QFont f = QApplication::font();
                    f.setPixelSize(12);
                    f.setWeight(QFont::Medium);
                    p.setFont(f);
                    p.setPen(QColor(QStringLiteral("#e2e8f0")));
                    p.drawText(QRectF(0, 0, sz.width(), sz.height()), Qt::AlignCenter, m_metaText);
                });
            drawTexture(m_statusPillRect, metaTex, dpr);
        }

        // 3. Search Bar + Refresh + Settings
        {
            const bool focused = m_searchFocused;
            drawGlassRect(m_searchRect, 22.0, focused ? QColor(15, 23, 42, 205) : QColor(15, 23, 42, 138),
                          focused ? QColor(61, 174, 233, 220) : QColor(255, 255, 255, 52), 1.0, dpr);

            const QString displayText = m_searchText + m_preeditText;
            const QString searchKey = QStringLiteral("search:%1|%2|%3|%4x%5@%6")
                                          .arg(m_searchText, m_preeditText)
                                          .arg(focused && m_cursorOn ? 1 : 0)
                                          .arg(static_cast<int>(m_searchRect.width()))
                                          .arg(static_cast<int>(m_searchRect.height()))
                                          .arg(dpr, 0, 'f', 2);
            const auto& searchTex =
                ensureTexture("search", searchKey, m_searchRect.size(), dpr, [&](QPainter& p, const QSizeF& sz) {
                    QFont f = QApplication::font();
                    f.setPixelSize(15);
                    p.setFont(f);
                    QFontMetricsF fm(f);
                    const qreal padL = 18.0;
                    const qreal padR = displayText.isEmpty() ? 18.0 : 38.0;
                    const QRectF textBox(padL, 0, std::max<qreal>(20.0, sz.width() - padL - padR), sz.height());
                    if (displayText.isEmpty()) {
                        p.setPen(QColor(203, 213, 225, 165));
                        p.drawText(textBox, Qt::AlignVCenter | Qt::AlignLeft, QStringLiteral("Search apps & tools..."));
                    } else {
                        p.setPen(QColor(QStringLiteral("#f8fafc")));
                        const qreal baseY = (sz.height() + fm.ascent() - fm.descent()) * 0.5;
                        p.drawText(QPointF(padL, baseY), m_searchText);
                        if (!m_preeditText.isEmpty()) {
                            const qreal preX = padL + fm.horizontalAdvance(m_searchText);
                            p.setPen(QColor(QStringLiteral("#7dd3fc")));
                            p.drawText(QPointF(preX, baseY), m_preeditText);
                            const qreal preW = fm.horizontalAdvance(m_preeditText);
                            p.fillRect(QRectF(preX, baseY + 2.0, preW, 1.5), QColor(QStringLiteral("#38bdf8")));
                        }
                    }
                    if (focused && m_cursorOn) {
                        const qreal cx = std::min(sz.width() - padR, padL + fm.horizontalAdvance(displayText) + 1.0);
                        p.fillRect(QRectF(cx, (sz.height() - 18.0) * 0.5, 2.0, 18.0),
                                   QColor(QStringLiteral("#38bdf8")));
                    }
                    if (!displayText.isEmpty()) {
                        const QRectF clearCircle(sz.width() - 32.0, (sz.height() - 22.0) * 0.5, 22.0, 22.0);
                        p.setPen(Qt::NoPen);
                        p.setBrush(QColor(148, 163, 184, 70));
                        p.drawEllipse(clearCircle);
                        p.setPen(QPen(QColor(QStringLiteral("#f8fafc")), 1.7, Qt::SolidLine, Qt::RoundCap));
                        const qreal cx = clearCircle.center().x();
                        const qreal cy = clearCircle.center().y();
                        p.drawLine(QPointF(cx - 4.0, cy - 4.0), QPointF(cx + 4.0, cy + 4.0));
                        p.drawLine(QPointF(cx + 4.0, cy - 4.0), QPointF(cx - 4.0, cy + 4.0));
                    }
                });
            drawTexture(m_searchRect, searchTex, dpr);

            drawIconCircleButton(m_refreshRect, "btn_refresh", QStringLiteral("↻"), m_hoverKind == HoverRefresh,
                                 m_pressKind == HoverRefresh, dpr);
            drawIconCircleButton(m_settingsRect, "btn_settings", QStringLiteral("⚙"), m_hoverKind == HoverSettings,
                                 m_pressKind == HoverSettings, dpr);
        }

        // 4. Category Pills (horizontally clipped)
        {
            pushScissor(m_catAreaRect, fbW, fbH, dpr);
            for (int i = 0; i < m_catRects.size() && i < m_categories.size(); ++i) {
                const QRectF r = m_catRects[i].translated(-m_catScrollX, 0);
                if (r.right() < m_catAreaRect.left() - 8 || r.left() > m_catAreaRect.right() + 8)
                    continue;
                const bool active = (m_categories[i] == m_activeCategory);
                const bool hover = (m_hoverKind == HoverCategory && m_hoverIndex == i);
                const bool press = (m_pressKind == HoverCategory && m_pressIndex == i);
                const QColor bg = active ? QColor(61, 174, 233, 222)
                                         : (press ? QColor(61, 174, 233, 110)
                                                  : (hover ? QColor(30, 41, 59, 165) : QColor(15, 23, 42, 112)));
                const QColor border = active ? QColor(147, 217, 255, 235) : QColor(255, 255, 255, 42);
                drawGlassRect(r, 18.0, bg, border, 1.0, dpr);

                const std::string slot = "cat_" + std::to_string(i);
                const QString key = QStringLiteral("cat:%1:%2:%3x%4@%5")
                                        .arg(m_categories[i])
                                        .arg(active ? 1 : 0)
                                        .arg(static_cast<int>(r.width()))
                                        .arg(static_cast<int>(r.height()))
                                        .arg(dpr, 0, 'f', 2);
                const auto& tex = ensureTexture(slot, key, r.size(), dpr, [&](QPainter& p, const QSizeF& sz) {
                    QFont f = QApplication::font();
                    f.setPixelSize(13);
                    f.setWeight(QFont::DemiBold);
                    p.setFont(f);
                    p.setPen(active ? Qt::white : QColor(QStringLiteral("#e2e8f0")));
                    p.drawText(QRectF(0, 0, sz.width(), sz.height()), Qt::AlignCenter, m_categories[i]);
                });
                drawTexture(r, tex, dpr);
            }
            m_gl.disable(GL_SCISSOR_TEST);
        }

        // 5. Scrollable App Grid (GPU Scissor + Cached Tile Textures)
        {
            pushScissor(m_gridAreaRect, fbW, fbH, dpr);
            if (m_filteredApps.isEmpty()) {
                const QRectF emptyR(m_gridAreaRect.left(), m_gridAreaRect.top() + 40, m_gridAreaRect.width(), 44);
                const QString key =
                    QStringLiteral("empty:%1@%2").arg(static_cast<int>(emptyR.width())).arg(dpr, 0, 'f', 2);
                const auto& tex = ensureTexture("empty", key, emptyR.size(), dpr, [&](QPainter& p, const QSizeF& sz) {
                    QFont f = QApplication::font();
                    f.setPixelSize(16);
                    p.setFont(f);
                    p.setPen(QColor(QStringLiteral("#cbd5e1")));
                    p.drawText(QRectF(0, 0, sz.width(), sz.height()), Qt::AlignCenter,
                               QStringLiteral("No matching applications"));
                });
                drawTexture(emptyR, tex, dpr);
            } else {
                for (int i = 0; i < m_filteredApps.size() && i < m_tileRects.size(); ++i) {
                    const QRectF r = m_tileRects[i].translated(0, -m_scrollY);
                    if (r.bottom() < m_gridAreaRect.top() - 16 || r.top() > m_gridAreaRect.bottom() + 16)
                        continue;

                    const bool hover = (m_hoverKind == HoverTile && m_hoverIndex == i);
                    const bool press = (m_pressKind == HoverTile && m_pressIndex == i);
                    const QColor bg =
                        press ? QColor(61, 174, 233, 108) : (hover ? QColor(30, 41, 59, 152) : QColor(15, 23, 42, 98));
                    const QColor border = press ? QColor(61, 174, 233, 255)
                                                : (hover ? QColor(61, 174, 233, 150) : QColor(255, 255, 255, 36));
                    drawGlassRect(r, 22.0, bg, border, 1.0, dpr);

                    const AppEntry& app = m_filteredApps[i];
                    const std::string slot = "tile_" + app.id.toStdString();
                    const QString key = QStringLiteral("tile:%1:%2:%3x%4@%5")
                                            .arg(app.id, app.name)
                                            .arg(static_cast<int>(r.width()))
                                            .arg(static_cast<int>(r.height()))
                                            .arg(dpr, 0, 'f', 2);
                    const auto& tex = ensureTexture(slot, key, r.size(), dpr, [&](QPainter& p, const QSizeF& sz) {
                        const qreal iconSize = m_compactLayout ? 48.0 : 52.0;
                        const qreal iconX = (sz.width() - iconSize) * 0.5;
                        const qreal iconY = 12.0;
                        const QIcon icon = resolveAppIcon(app.iconName, app.name);
                        const QPixmap pm =
                            icon.pixmap(QSize(static_cast<int>(iconSize), static_cast<int>(iconSize)), dpr);
                        p.drawPixmap(QRectF(iconX, iconY, iconSize, iconSize), pm, pm.rect());

                        QFont f = QApplication::font();
                        f.setPixelSize(12);
                        f.setWeight(QFont::DemiBold);
                        p.setFont(f);
                        p.setPen(QColor(QStringLiteral("#f8fafc")));
                        const QRectF labelRect(6.0, iconY + iconSize + 6.0, sz.width() - 12.0,
                                               std::max<qreal>(18.0, sz.height() - iconY - iconSize - 10.0));
                        const QFontMetricsF fm(f);
                        // Allow 2 clean lines on desktop/mobile before eliding so names like PCManFM-Qt / SuperTuxKart
                        // stay legible.
                        QString label = app.name;
                        if (fm.horizontalAdvance(label) > labelRect.width() * 1.92) {
                            label = fm.elidedText(label, Qt::ElideRight, labelRect.width() * 1.85);
                        }
                        p.drawText(labelRect, Qt::AlignHCenter | Qt::AlignTop | Qt::TextWordWrap, label);
                    });
                    drawTexture(r, tex, dpr);
                }
            }

            // Thin GPU scrollbar thumb when grid overflows
            if (m_maxScrollY > 2.0) {
                const qreal viewH = m_gridAreaRect.height();
                const qreal contentH = viewH + m_maxScrollY;
                const qreal thumbH = std::max<qreal>(32.0, viewH * (viewH / contentH));
                const qreal ratio = std::clamp(m_scrollY / m_maxScrollY, 0.0, 1.0);
                const qreal thumbY = m_gridAreaRect.top() + 4.0 + ratio * (viewH - thumbH - 8.0);
                const QRectF barRect(m_gridAreaRect.right() - 6.0, thumbY, 4.0, thumbH);
                drawGlassRect(barRect, 2.0, QColor(255, 255, 255, 86), Qt::transparent, 0.0, dpr);
            }
            m_gl.disable(GL_SCISSOR_TEST);
        }

        // 6. Floating Frosted Bottom Dock
        if (!m_dockItems.isEmpty()) {
            drawGlassRect(m_dockRect, 24.0, QColor(12, 20, 38, 152), QColor(255, 255, 255, 48), 1.0, dpr);
            for (int i = 0; i < m_dockItems.size() && i < m_dockItemRects.size(); ++i) {
                const QRectF r = m_dockItemRects[i];
                const bool hover = (m_hoverKind == HoverDock && m_hoverIndex == i);
                const bool press = (m_pressKind == HoverDock && m_pressIndex == i);
                const QColor bg =
                    press ? QColor(61, 174, 233, 128) : (hover ? QColor(61, 174, 233, 74) : QColor(255, 255, 255, 22));
                const QColor border = hover || press ? QColor(61, 174, 233, 175) : QColor(255, 255, 255, 32);
                drawGlassRect(r, 16.0, bg, border, 1.0, dpr);

                const auto& item = m_dockItems[i];
                const std::string slot = "dock_" + item.entry.id.toStdString();
                const QString key = QStringLiteral("dock:%1:%2:%3:%4x%5@%6")
                                        .arg(item.entry.id, item.label)
                                        .arg(m_compactLayout ? 1 : 0)
                                        .arg(static_cast<int>(r.width()))
                                        .arg(static_cast<int>(r.height()))
                                        .arg(dpr, 0, 'f', 2);
                const auto& tex = ensureTexture(slot, key, r.size(), dpr, [&](QPainter& p, const QSizeF& sz) {
                    const QIcon icon = resolveAppIcon(item.entry.iconName, item.label);
                    if (m_compactLayout) {
                        const qreal iconSz = 30.0;
                        const QPixmap pm = icon.pixmap(QSize(30, 30), dpr);
                        p.drawPixmap(QRectF((sz.width() - iconSz) * 0.5, (sz.height() - iconSz) * 0.5, iconSz, iconSz),
                                     pm, pm.rect());
                    } else {
                        const qreal iconSz = 26.0;
                        const QPixmap pm = icon.pixmap(QSize(26, 26), dpr);
                        p.drawPixmap(QRectF(12.0, (sz.height() - iconSz) * 0.5, iconSz, iconSz), pm, pm.rect());
                        QFont f = QApplication::font();
                        f.setPixelSize(12);
                        f.setWeight(QFont::DemiBold);
                        p.setFont(f);
                        p.setPen(QColor(QStringLiteral("#f8fafc")));
                        p.drawText(QRectF(44.0, 0, sz.width() - 52.0, sz.height()), Qt::AlignVCenter | Qt::AlignLeft,
                                   item.label);
                    }
                });
                drawTexture(r, tex, dpr);
            }
        }

        // 7. Floating Toast Notification
        if (!m_toastText.isEmpty()) {
            QFont f = QApplication::font();
            f.setPixelSize(13);
            f.setWeight(QFont::DemiBold);
            QFontMetricsF fm(f);
            const qreal tw =
                std::clamp(fm.horizontalAdvance(m_toastText) + 40.0, 140.0, std::max<qreal>(180.0, width() - 32.0));
            const qreal th = 40.0;
            const QRectF toastR((width() - tw) * 0.5, std::max<qreal>(16.0, m_dockRect.top() - th - 14.0), tw, th);
            drawGlassRect(toastR, 16.0, QColor(12, 20, 38, 238), QColor(61, 174, 233, 180), 1.0, dpr);
            const QString key =
                QStringLiteral("toast:%1:%2@%3").arg(m_toastText).arg(static_cast<int>(tw)).arg(dpr, 0, 'f', 2);
            const auto& tex = ensureTexture("toast", key, toastR.size(), dpr, [&](QPainter& p, const QSizeF& sz) {
                p.setFont(f);
                p.setPen(QColor(QStringLiteral("#f8fafc")));
                p.drawText(QRectF(0, 0, sz.width(), sz.height()), Qt::AlignCenter, m_toastText);
            });
            drawTexture(toastR, tex, dpr);
        }

        m_gl.disableVertexAttribArray(0);
        m_gl.bindBuffer(GL_ARRAY_BUFFER, 0);
        m_gl.useProgram(0);
    }

    bool event(QEvent* ev) override {
        if (ev->type() == QEvent::InputMethodQuery) {
            auto* q = static_cast<QInputMethodQueryEvent*>(ev);
            handleInputMethodQuery(q);
            return true;
        }
        if (ev->type() == QEvent::InputMethod) {
            auto* ime = static_cast<QInputMethodEvent*>(ev);
            handleInputMethodEvent(ime);
            return true;
        }
        return QOpenGLWindow::event(ev);
    }

    void mousePressEvent(QMouseEvent* ev) override {
        if (ev->button() != Qt::LeftButton)
            return;
        const QPointF pos = ev->position();
        m_pointerDown = true;
        m_dragging = false;
        m_dragTarget = DragNone;
        m_pressPos = pos;
        m_lastPointerPos = pos;
        m_scrollVelY = 0.0;
        m_lastDragTimer.restart();

        if (m_gridAreaRect.contains(pos)) {
            m_dragTarget = DragGrid;
        } else if (m_catAreaRect.contains(pos) && m_maxCatScrollX > 1.0) {
            m_dragTarget = DragCategories;
        }

        hitTest(pos, m_pressKind, m_pressIndex);
        requestRender();
    }

    void mouseMoveEvent(QMouseEvent* ev) override {
        const QPointF pos = ev->position();
        if (m_pointerDown) {
            const QPointF totalDelta = pos - m_pressPos;
            if (!m_dragging && std::hypot(totalDelta.x(), totalDelta.y()) > 8.0) {
                m_dragging = true;
                m_pressKind = HoverNone;
                m_pressIndex = -1;
            }
            if (m_dragging) {
                const qreal dt = std::max(0.004, m_lastDragTimer.restart() / 1000.0);
                const QPointF step = pos - m_lastPointerPos;
                if (m_dragTarget == DragGrid) {
                    const qreal resistance = (m_scrollY < 0.0 || m_scrollY > m_maxScrollY) ? 0.35 : 1.0;
                    m_scrollY -= step.y() * resistance;
                    const qreal instVel = (-step.y()) / dt;
                    m_scrollVelY = m_scrollVelY * 0.45 + instVel * 0.55;
                } else if (m_dragTarget == DragCategories) {
                    m_catScrollX = std::clamp(m_catScrollX - step.x(), 0.0, m_maxCatScrollX);
                }
                m_lastPointerPos = pos;
                requestRender();
                return;
            }
        } else {
            HoverKind hk = HoverNone;
            int hi = -1;
            hitTest(pos, hk, hi);
            if (hk != m_hoverKind || hi != m_hoverIndex) {
                m_hoverKind = hk;
                m_hoverIndex = hi;
                setCursor(hk == HoverNone ? Qt::ArrowCursor
                                          : (hk == HoverSearch ? Qt::IBeamCursor : Qt::PointingHandCursor));
                requestRender();
            }
        }
        m_lastPointerPos = pos;
    }

    void mouseReleaseEvent(QMouseEvent* ev) override {
        if (ev->button() != Qt::LeftButton)
            return;
        const QPointF pos = ev->position();
        const bool wasDragging = m_dragging;
        const DragTarget target = m_dragTarget;
        m_pointerDown = false;
        m_dragging = false;
        m_dragTarget = DragNone;
        m_pressKind = HoverNone;
        m_pressIndex = -1;

        if (wasDragging) {
            if (target == DragGrid && (std::abs(m_scrollVelY) > 40.0 || m_scrollY < 0.0 || m_scrollY > m_maxScrollY)) {
                m_physTimer.restart();
                if (!m_animTimer->isActive())
                    m_animTimer->start();
            }
            requestRender();
            return;
        }

        handleTap(pos);
        requestRender();
    }

    void wheelEvent(QWheelEvent* ev) override {
        const QPointF pos = ev->position();
        qreal dy = 0.0;
        if (!ev->pixelDelta().isNull()) {
            dy = ev->pixelDelta().y();
        } else {
            dy = ev->angleDelta().y() * 0.5;
        }
        if (std::abs(dy) < 0.1)
            return;

        if (m_catAreaRect.contains(pos) && m_maxCatScrollX > 1.0) {
            m_catScrollX = std::clamp(m_catScrollX - dy, 0.0, m_maxCatScrollX);
        } else {
            m_scrollVelY = 0.0;
            m_scrollY = std::clamp(m_scrollY - dy, 0.0, m_maxScrollY);
        }
        requestRender();
    }

    void keyPressEvent(QKeyEvent* ev) override {
        if (ev->key() == Qt::Key_Escape) {
            if (!m_searchText.isEmpty() || !m_preeditText.isEmpty()) {
                m_searchText.clear();
                m_preeditText.clear();
                rebuildFilteredApps();
                notifyInputMethod();
                requestRender();
            } else if (m_searchFocused) {
                setSearchFocused(false);
            }
            return;
        }
        if (ev->key() == Qt::Key_Backspace) {
            if (!m_searchText.isEmpty()) {
                m_searchText.chop(1);
                rebuildFilteredApps();
                notifyInputMethod();
                requestRender();
            }
            return;
        }
        if (ev->key() == Qt::Key_Return || ev->key() == Qt::Key_Enter) {
            if (!m_filteredApps.isEmpty()) {
                launchApp(m_filteredApps.first());
            }
            return;
        }
        const QString txt = ev->text();
        if (!txt.isEmpty() && txt.at(0).isPrint() &&
            !(ev->modifiers() & (Qt::ControlModifier | Qt::AltModifier | Qt::MetaModifier))) {
            if (!m_searchFocused)
                setSearchFocused(true);
            m_searchText.append(txt);
            rebuildFilteredApps();
            notifyInputMethod();
            requestRender();
        }
    }

private:
    enum HoverKind {
        HoverNone = 0,
        HoverSearch,
        HoverSearchClear,
        HoverRefresh,
        HoverSettings,
        HoverCategory,
        HoverTile,
        HoverDock,
    };

    enum DragTarget {
        DragNone = 0,
        DragGrid,
        DragCategories,
    };

    struct DockItem {
        QString label;
        AppEntry entry;
    };

    void requestRender() {
        if (isExposed())
            update();
    }

    void setSearchFocused(bool focused) {
        if (m_searchFocused == focused)
            return;
        m_searchFocused = focused;
        m_cursorOn = focused;
        if (focused) {
            m_cursorBlinkTimer->start();
            notifyInputMethod();
            QGuiApplication::inputMethod()->show();
        } else {
            m_cursorBlinkTimer->stop();
            m_preeditText.clear();
            notifyInputMethod();
            QGuiApplication::inputMethod()->hide();
        }
        requestRender();
    }

    void notifyInputMethod() {
        QGuiApplication::inputMethod()->update(Qt::ImEnabled | Qt::ImCursorRectangle | Qt::ImSurroundingText |
                                               Qt::ImCursorPosition | Qt::ImAnchorPosition);
    }

    void handleInputMethodQuery(QInputMethodQueryEvent* q) {
        const Qt::InputMethodQueries queries = q->queries();
        if (queries & Qt::ImEnabled)
            q->setValue(Qt::ImEnabled, m_searchFocused);
        if (queries & Qt::ImCursorRectangle) {
            QFont f = QApplication::font();
            f.setPixelSize(15);
            QFontMetricsF fm(f);
            const qreal cx = std::min(m_searchRect.right() - 20.0,
                                      m_searchRect.left() + 18.0 + fm.horizontalAdvance(m_searchText + m_preeditText));
            q->setValue(Qt::ImCursorRectangle, QRectF(cx, m_searchRect.top() + 8.0, 2.0, m_searchRect.height() - 16.0));
        }
        if (queries & Qt::ImSurroundingText)
            q->setValue(Qt::ImSurroundingText, m_searchText);
        if (queries & Qt::ImCursorPosition)
            q->setValue(Qt::ImCursorPosition, m_searchText.size());
        if (queries & Qt::ImAnchorPosition)
            q->setValue(Qt::ImAnchorPosition, m_searchText.size());
        q->accept();
    }

    void handleInputMethodEvent(QInputMethodEvent* ev) {
        if (!m_searchFocused)
            setSearchFocused(true);
        if (ev->replacementLength() > 0) {
            const int start = std::clamp(static_cast<int>(m_searchText.size()) + ev->replacementStart(), 0,
                                         static_cast<int>(m_searchText.size()));
            m_searchText.remove(start, ev->replacementLength());
        }
        if (!ev->commitString().isEmpty()) {
            m_searchText.append(ev->commitString());
        }
        m_preeditText = ev->preeditString();
        rebuildFilteredApps();
        notifyInputMethod();
        requestRender();
        ev->accept();
    }

    void stepPhysics() {
        const qreal dt = std::clamp(m_physTimer.restart() / 1000.0, 0.004, 0.05);
        bool active = false;

        if (!m_pointerDown) {
            if (std::abs(m_scrollVelY) > 8.0) {
                m_scrollY += m_scrollVelY * dt;
                m_scrollVelY *= std::exp(-6.5 * dt);
                active = true;
            } else {
                m_scrollVelY = 0.0;
            }

            if (m_scrollY < 0.0) {
                m_scrollY *= std::exp(-16.0 * dt);
                m_scrollVelY *= 0.5;
                if (std::abs(m_scrollY) < 0.5)
                    m_scrollY = 0.0;
                else
                    active = true;
            } else if (m_scrollY > m_maxScrollY) {
                const qreal over = m_scrollY - m_maxScrollY;
                m_scrollY = m_maxScrollY + over * std::exp(-16.0 * dt);
                m_scrollVelY *= 0.5;
                if (std::abs(m_scrollY - m_maxScrollY) < 0.5)
                    m_scrollY = m_maxScrollY;
                else
                    active = true;
            }
        }

        if (!active)
            m_animTimer->stop();
        requestRender();
    }

    void updateStatus() {
        const QDateTime now = QDateTime::currentDateTime();
        m_clockText = now.toString(QStringLiteral("HH:mm"));
        m_metaText = now.toString(QStringLiteral("ddd, MMM d")) + QStringLiteral("  •  ") + readMemorySummary();
    }

    void reloadApps() {
        m_allApps = scanDesktopEntries();
        rebuildDockItems();
        rebuildFilteredApps();
        if (m_settingsWindow)
            m_settingsWindow->refreshApps(m_allApps);
    }

    void rebuildDockItems() {
        m_dockItems.clear();
        struct QuickTarget {
            QString id;
            QString label;
            QString fallbackExec;
            QString fallbackIcon;
        };
        const QList<QuickTarget> targets = {
            {QStringLiteral("qterminal"), QStringLiteral("Terminal"), QStringLiteral("qterminal"),
             QStringLiteral("utilities-terminal")},
            {QStringLiteral("pcmanfm-qt"), QStringLiteral("Files"), QStringLiteral("pcmanfm-qt"),
             QStringLiteral("system-file-manager")},
            {QStringLiteral("featherpad"), QStringLiteral("Editor"), QStringLiteral("featherpad"),
             QStringLiteral("featherpad")},
            {QStringLiteral("lximage-qt"), QStringLiteral("Images"), QStringLiteral("lximage-qt"),
             QStringLiteral("lximage-qt")},
            {QStringLiteral("qps"), QStringLiteral("Monitor"), QStringLiteral("qps"), QStringLiteral("qps")},
            {QStringLiteral("lxqt-config"), QStringLiteral("Settings"), QStringLiteral("lxqt-config"),
             QStringLiteral("preferences-system")},
        };
        for (const QuickTarget& t : targets) {
            AppEntry entry;
            bool found = false;
            for (const AppEntry& app : m_allApps) {
                if (app.id == t.id) {
                    entry = app;
                    found = true;
                    break;
                }
            }
            if (!found) {
                if (resolveExecutablePath(t.fallbackExec).isEmpty())
                    continue;
                entry.id = t.id;
                entry.name = t.label;
                entry.execLine = t.fallbackExec;
                entry.iconName = t.fallbackIcon;
            }
            m_dockItems.append({t.label, entry});
        }
    }

    void rebuildFilteredApps() {
        m_filteredApps.clear();
        const QString query = (m_searchText + m_preeditText).trimmed();
        for (const AppEntry& app : m_allApps) {
            if (m_activeCategory == QStringLiteral("Favorites") && !app.pinned)
                continue;
            if (m_activeCategory != QStringLiteral("All") && m_activeCategory != QStringLiteral("Favorites") &&
                app.category != m_activeCategory) {
                continue;
            }
            if (!query.isEmpty() && !app.name.contains(query, Qt::CaseInsensitive) &&
                !app.comment.contains(query, Qt::CaseInsensitive) && !app.id.contains(query, Qt::CaseInsensitive)) {
                continue;
            }
            m_filteredApps.append(app);
        }
        m_scrollY = 0.0;
        m_scrollVelY = 0.0;
        recomputeLayout();
    }

    void recomputeLayout() {
        const qreal W = std::max<qreal>(320.0, width());
        const qreal H = std::max<qreal>(420.0, height());
        m_compactLayout = m_mobileMode || W < 580.0;

        const qreal sideMargin = m_compactLayout ? 16.0 : 28.0;
        qreal y = m_compactLayout ? 16.0 : 22.0;

        // Hero Clock
        const qreal clockH = m_compactLayout ? 54.0 : 60.0;
        m_clockRect = QRectF((W - 220.0) * 0.5, y, 220.0, clockH);
        y += clockH + 4.0;

        // Status Pill
        QFont metaFont = QApplication::font();
        metaFont.setPixelSize(12);
        metaFont.setWeight(QFont::Medium);
        const qreal metaW = std::clamp(QFontMetricsF(metaFont).horizontalAdvance(m_metaText) + 32.0, 180.0, W - 32.0);
        m_statusPillRect = QRectF((W - metaW) * 0.5, y, metaW, 28.0);
        y += 28.0 + 14.0;

        // Search Row (centered max 680px on wide desktop)
        const qreal rowW = m_compactLayout ? (W - sideMargin * 2.0) : std::min<qreal>(680.0, W - sideMargin * 2.0);
        const qreal rowX = (W - rowW) * 0.5;
        const qreal btnSz = 44.0;
        const qreal gap = 8.0;
        m_settingsRect = QRectF(rowX + rowW - btnSz, y, btnSz, btnSz);
        m_refreshRect = QRectF(m_settingsRect.left() - gap - btnSz, y, btnSz, btnSz);
        m_searchRect = QRectF(rowX, y, std::max<qreal>(120.0, m_refreshRect.left() - gap - rowX), btnSz);
        m_searchClearRect = QRectF(m_searchRect.right() - 36.0, m_searchRect.top() + 4.0, 32.0, 36.0);
        y += btnSz + 12.0;

        // Category Pills
        QFont catFont = QApplication::font();
        catFont.setPixelSize(13);
        catFont.setWeight(QFont::DemiBold);
        QFontMetricsF catFm(catFont);
        m_catRects.clear();
        const qreal catH = 36.0;
        qreal totalCatW = 0.0;
        QList<qreal> pillWidths;
        for (const QString& c : m_categories) {
            const qreal pw = std::max<qreal>(54.0, catFm.horizontalAdvance(c) + 30.0);
            pillWidths.append(pw);
            totalCatW += pw;
        }
        if (m_categories.size() > 1)
            totalCatW += (m_categories.size() - 1) * 8.0;

        const qreal catAreaW = m_compactLayout ? (W - sideMargin * 2.0) : std::min<qreal>(960.0, W - sideMargin * 2.0);
        const qreal catAreaX = (W - catAreaW) * 0.5;
        m_catAreaRect = QRectF(catAreaX, y, catAreaW, catH);
        qreal cx = (totalCatW <= catAreaW) ? (catAreaX + (catAreaW - totalCatW) * 0.5) : catAreaX;
        for (qreal pw : pillWidths) {
            m_catRects.append(QRectF(cx, y, pw, catH));
            cx += pw + 8.0;
        }
        m_maxCatScrollX = std::max<qreal>(0.0, totalCatW - catAreaW);
        m_catScrollX = std::clamp(m_catScrollX, 0.0, m_maxCatScrollX);
        y += catH + 14.0;

        // Bottom Dock
        const qreal dockH = 72.0;
        const qreal dockBottomMargin = m_compactLayout ? 12.0 : 18.0;
        const qreal dockTop = std::max(y + 120.0, H - dockBottomMargin - dockH);
        const int nDock = m_dockItems.size();
        const qreal dockGap = 10.0;
        const qreal itemH = 52.0;
        const qreal itemW =
            m_compactLayout
                ? std::clamp((W - sideMargin * 2.0 - 20.0 - std::max(0, nDock - 1) * dockGap) / std::max(1, nDock),
                             44.0, 58.0)
                : 112.0;
        const qreal dockContentW = nDock * itemW + std::max(0, nDock - 1) * dockGap;
        const qreal dockW = std::min(W - sideMargin * 2.0, dockContentW + 24.0);
        m_dockRect = QRectF((W - dockW) * 0.5, dockTop, dockW, dockH);
        m_dockItemRects.clear();
        qreal dx = m_dockRect.left() + (dockW - dockContentW) * 0.5;
        const qreal dy = dockTop + (dockH - itemH) * 0.5;
        for (int i = 0; i < nDock; ++i) {
            m_dockItemRects.append(QRectF(dx, dy, itemW, itemH));
            dx += itemW + dockGap;
        }

        // App Grid Area
        const qreal gridMaxW = m_compactLayout ? (W - sideMargin * 2.0) : std::min<qreal>(1040.0, W - sideMargin * 2.0);
        const qreal gridX = (W - gridMaxW) * 0.5;
        const qreal gridH = std::max<qreal>(80.0, dockTop - 12.0 - y);
        m_gridAreaRect = QRectF(gridX, y, gridMaxW, gridH);

        const qreal tileGapX = m_compactLayout ? 12.0 : 16.0;
        const qreal tileGapY = m_compactLayout ? 14.0 : 16.0;
        const qreal innerGridW = std::max<qreal>(260.0, gridMaxW - 10.0);
        const int cols =
            m_compactLayout ? 3 : std::clamp(static_cast<int>((innerGridW + tileGapX) / (132.0 + tileGapX)), 4, 8);
        const qreal tileW = std::clamp((innerGridW - (cols - 1) * tileGapX) / cols, 92.0, 142.0);
        const qreal tileH = m_compactLayout ? std::clamp(tileW + 4.0, 100.0, 128.0) : 118.0;
        const qreal usedRowW = cols * tileW + (cols - 1) * tileGapX;
        const qreal startX = gridX + (gridMaxW - usedRowW) * 0.5;

        m_tileRects.clear();
        m_tileRects.reserve(m_filteredApps.size());
        for (int i = 0; i < m_filteredApps.size(); ++i) {
            const int r = i / cols;
            const int c = i % cols;
            m_tileRects.append(QRectF(startX + c * (tileW + tileGapX), y + 4.0 + r * (tileH + tileGapY), tileW, tileH));
        }

        const int rows = (m_filteredApps.size() + cols - 1) / std::max(1, cols);
        const qreal contentH = rows > 0 ? (rows * tileH + (rows - 1) * tileGapY + 20.0) : 0.0;
        m_maxScrollY = std::max<qreal>(0.0, contentH - gridH);
        m_scrollY = std::clamp(m_scrollY, 0.0, m_maxScrollY);
    }

    void hitTest(const QPointF& pos, HoverKind& kindOut, int& indexOut) const {
        kindOut = HoverNone;
        indexOut = -1;

        if (!m_searchText.isEmpty() && m_searchClearRect.contains(pos)) {
            kindOut = HoverSearchClear;
            return;
        }
        if (m_searchRect.contains(pos)) {
            kindOut = HoverSearch;
            return;
        }
        if (m_refreshRect.contains(pos)) {
            kindOut = HoverRefresh;
            return;
        }
        if (m_settingsRect.contains(pos)) {
            kindOut = HoverSettings;
            return;
        }
        if (m_catAreaRect.contains(pos)) {
            for (int i = 0; i < m_catRects.size(); ++i) {
                if (m_catRects[i].translated(-m_catScrollX, 0).contains(pos)) {
                    kindOut = HoverCategory;
                    indexOut = i;
                    return;
                }
            }
        }
        if (m_gridAreaRect.contains(pos)) {
            for (int i = 0; i < m_tileRects.size(); ++i) {
                if (m_tileRects[i].translated(0, -m_scrollY).contains(pos)) {
                    kindOut = HoverTile;
                    indexOut = i;
                    return;
                }
            }
        }
        if (m_dockRect.contains(pos)) {
            for (int i = 0; i < m_dockItemRects.size(); ++i) {
                if (m_dockItemRects[i].contains(pos)) {
                    kindOut = HoverDock;
                    indexOut = i;
                    return;
                }
            }
        }
    }

    void handleTap(const QPointF& pos) {
        HoverKind hk = HoverNone;
        int idx = -1;
        hitTest(pos, hk, idx);

        if (hk == HoverSearchClear) {
            m_searchText.clear();
            m_preeditText.clear();
            rebuildFilteredApps();
            notifyInputMethod();
            return;
        }
        if (hk == HoverSearch) {
            setSearchFocused(true);
            return;
        }
        if (m_searchFocused) {
            setSearchFocused(false);
        }

        switch (hk) {
        case HoverRefresh:
            reloadApps();
            showToast(QStringLiteral("Applications refreshed"));
            break;
        case HoverSettings:
            openSettingsWindow();
            break;
        case HoverCategory:
            if (idx >= 0 && idx < m_categories.size()) {
                m_activeCategory = m_categories[idx];
                rebuildFilteredApps();
            }
            break;
        case HoverTile:
            if (idx >= 0 && idx < m_filteredApps.size()) {
                launchApp(m_filteredApps[idx]);
            }
            break;
        case HoverDock:
            if (idx >= 0 && idx < m_dockItems.size()) {
                launchApp(m_dockItems[idx].entry);
            }
            break;
        default:
            break;
        }
    }

    void launchApp(const AppEntry& app) {
        if (launchDesktopEntry(app)) {
            showToast(QStringLiteral("Launching %1...").arg(app.name));
        } else {
            showToast(QStringLiteral("Failed to start %1").arg(app.name));
        }
    }

    void openSettingsWindow() {
        if (m_settingsWindow) {
            m_settingsWindow->refreshApps(m_allApps);
            m_settingsWindow->show();
            m_settingsWindow->raise();
            m_settingsWindow->activateWindow();
            return;
        }
        m_settingsWindow = new SettingsWindow(m_allApps, m_mobileMode, nullptr);
        if (m_mobileMode) {
            m_settingsWindow->showMaximized();
        } else {
            m_settingsWindow->show();
        }
    }

    void showToast(const QString& text) {
        m_toastText = text;
        m_toastTimer->start(2200);
        requestRender();
    }

    // ---- GL Drawing Helpers ----

    void drawWallpaper(int fbW, int fbH) {
        m_gl.uniform1i(m_uMode, 0);
        m_gl.uniform4f(m_uRect, 0.f, 0.f, static_cast<float>(fbW), static_cast<float>(fbH));
        m_gl.drawArrays(GL_TRIANGLE_STRIP, 0, 4);
    }

    void drawGlassRect(const QRectF& logicalRect, qreal radiusLogical, const QColor& fill, const QColor& border,
                       qreal borderLogical, qreal dpr) {
        const float x = static_cast<float>(logicalRect.left() * dpr);
        const float y = static_cast<float>(logicalRect.top() * dpr);
        const float w = static_cast<float>(logicalRect.width() * dpr);
        const float h = static_cast<float>(logicalRect.height() * dpr);
        if (w <= 0.5f || h <= 0.5f)
            return;
        m_gl.uniform1i(m_uMode, 1);
        m_gl.uniform4f(m_uRect, x, y, w, h);
        m_gl.uniform4f(m_uColor, fill.redF(), fill.greenF(), fill.blueF(), fill.alphaF());
        m_gl.uniform4f(m_uBorderColor, border.redF(), border.greenF(), border.blueF(), border.alphaF());
        m_gl.uniform4f(m_uParams, static_cast<float>(radiusLogical * dpr), static_cast<float>(borderLogical * dpr),
                       1.0f, 0.0f);
        m_gl.drawArrays(GL_TRIANGLE_STRIP, 0, 4);
    }

    void drawTexture(const QRectF& logicalRect, const GlTextureEntry& tex, qreal dpr, float alpha = 1.0f) {
        if (tex.id == 0)
            return;
        const float x = static_cast<float>(logicalRect.left() * dpr);
        const float y = static_cast<float>(logicalRect.top() * dpr);
        const float w = static_cast<float>(logicalRect.width() * dpr);
        const float h = static_cast<float>(logicalRect.height() * dpr);
        m_gl.activeTexture(GL_TEXTURE0);
        m_gl.bindTexture(GL_TEXTURE_2D, tex.id);
        m_gl.uniform1i(m_uMode, 2);
        m_gl.uniform4f(m_uRect, x, y, w, h);
        m_gl.uniform4f(m_uParams, 0.f, 0.f, alpha, 0.f);
        m_gl.drawArrays(GL_TRIANGLE_STRIP, 0, 4);
    }

    void drawIconCircleButton(const QRectF& rect, const std::string& slot, const QString& glyph, bool hover, bool press,
                              qreal dpr) {
        const QColor bg =
            press ? QColor(61, 174, 233, 128) : (hover ? QColor(30, 41, 59, 175) : QColor(15, 23, 42, 138));
        const QColor border = press || hover ? QColor(61, 174, 233, 210) : QColor(255, 255, 255, 52);
        drawGlassRect(rect, rect.height() * 0.5, bg, border, 1.0, dpr);
        const QString key =
            QStringLiteral("btn:%1:%2@%3").arg(glyph).arg(static_cast<int>(rect.width())).arg(dpr, 0, 'f', 2);
        const auto& tex = ensureTexture(slot, key, rect.size(), dpr, [&](QPainter& p, const QSizeF& sz) {
            QFont f = QApplication::font();
            f.setPixelSize(19);
            f.setWeight(QFont::DemiBold);
            p.setFont(f);
            p.setPen(QColor(QStringLiteral("#e2e8f0")));
            p.drawText(QRectF(0, 0, sz.width(), sz.height()), Qt::AlignCenter, glyph);
        });
        drawTexture(rect, tex, dpr);
    }

    void pushScissor(const QRectF& logicalRect, int fbW, int fbH, qreal dpr) {
        const int sx = std::clamp(static_cast<int>(std::floor(logicalRect.left() * dpr)), 0, fbW);
        const int syTop = std::clamp(static_cast<int>(std::floor(logicalRect.top() * dpr)), 0, fbH);
        const int sxEnd = std::clamp(static_cast<int>(std::ceil(logicalRect.right() * dpr)), sx, fbW);
        const int syEnd = std::clamp(static_cast<int>(std::ceil(logicalRect.bottom() * dpr)), syTop, fbH);
        const int sw = std::max(0, sxEnd - sx);
        const int sh = std::max(0, syEnd - syTop);
        const int glY = std::max(0, fbH - syEnd);
        m_gl.enable(GL_SCISSOR_TEST);
        m_gl.scissor(sx, glY, sw, sh);
    }

    template <typename PaintFn>
    const GlTextureEntry& ensureTexture(const std::string& slot, const QString& key, const QSizeF& logicalSize,
                                        qreal dpr, PaintFn&& paintFn) {
        GlTextureEntry& entry = m_textures[slot];
        const int pxW = std::max(1, static_cast<int>(std::ceil(logicalSize.width() * dpr)));
        const int pxH = std::max(1, static_cast<int>(std::ceil(logicalSize.height() * dpr)));
        if (entry.id != 0 && entry.key == key && entry.pxW == pxW && entry.pxH == pxH) {
            return entry;
        }

        QImage img(pxW, pxH, QImage::Format_RGBA8888_Premultiplied);
        img.setDevicePixelRatio(dpr);
        img.fill(Qt::transparent);
        {
            QPainter p(&img);
            p.setRenderHint(QPainter::Antialiasing, true);
            p.setRenderHint(QPainter::SmoothPixmapTransform, true);
            p.setRenderHint(QPainter::TextAntialiasing, true);
            paintFn(p, logicalSize);
        }

        if (entry.id == 0) {
            m_gl.genTextures(1, &entry.id);
        }
        m_gl.bindTexture(GL_TEXTURE_2D, entry.id);
        m_gl.texParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        m_gl.texParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        m_gl.texParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        m_gl.texParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        m_gl.pixelStorei(GL_UNPACK_ALIGNMENT, 4);
        m_gl.texImage2D(GL_TEXTURE_2D, 0, GL_RGBA, pxW, pxH, 0, GL_RGBA, GL_UNSIGNED_BYTE, img.constBits());
        m_gl.bindTexture(GL_TEXTURE_2D, 0);

        entry.pxW = pxW;
        entry.pxH = pxH;
        entry.key = key;
        return entry;
    }

    void destroyAllTextures() {
        for (auto& kv : m_textures) {
            if (kv.second.id != 0)
                m_gl.deleteTextures(1, &kv.second.id);
        }
        m_textures.clear();
    }

    bool m_mobileMode = false;
    bool m_compactLayout = false;
    bool m_glReady = false;
    qreal m_lastDpr = 1.0;

    LauncherGlApi m_gl;
    GLuint m_program = 0;
    GLuint m_quadVbo = 0;
    int m_uViewport = -1;
    int m_uRect = -1;
    int m_uMode = -1;
    int m_uColor = -1;
    int m_uBorderColor = -1;
    int m_uParams = -1;
    int m_uTex = -1;
    std::unordered_map<std::string, GlTextureEntry> m_textures;

    QString m_clockText = QStringLiteral("12:00");
    QString m_metaText = QStringLiteral("Debian i386");
    QString m_searchText;
    QString m_preeditText;
    bool m_searchFocused = false;
    bool m_cursorOn = false;
    QString m_toastText;

    QStringList m_categories;
    QString m_activeCategory = QStringLiteral("All");
    QList<AppEntry> m_allApps;
    QList<AppEntry> m_filteredApps;
    QList<DockItem> m_dockItems;

    QRectF m_clockRect;
    QRectF m_statusPillRect;
    QRectF m_searchRect;
    QRectF m_searchClearRect;
    QRectF m_refreshRect;
    QRectF m_settingsRect;
    QRectF m_catAreaRect;
    QList<QRectF> m_catRects;
    QRectF m_gridAreaRect;
    QList<QRectF> m_tileRects;
    QRectF m_dockRect;
    QList<QRectF> m_dockItemRects;

    qreal m_scrollY = 0.0;
    qreal m_maxScrollY = 0.0;
    qreal m_scrollVelY = 0.0;
    qreal m_catScrollX = 0.0;
    qreal m_maxCatScrollX = 0.0;

    bool m_pointerDown = false;
    bool m_dragging = false;
    DragTarget m_dragTarget = DragNone;
    QPointF m_pressPos;
    QPointF m_lastPointerPos;
    QElapsedTimer m_lastDragTimer;
    QElapsedTimer m_physTimer;

    HoverKind m_hoverKind = HoverNone;
    int m_hoverIndex = -1;
    HoverKind m_pressKind = HoverNone;
    int m_pressIndex = -1;

    QTimer* m_clockTimer = nullptr;
    QTimer* m_toastTimer = nullptr;
    QTimer* m_animTimer = nullptr;
    QTimer* m_cursorBlinkTimer = nullptr;
    QPointer<SettingsWindow> m_settingsWindow;
};

} // namespace

int main(int argc, char* argv[]) {
    QSurfaceFormat fmt;
#ifdef PODISH_LAUNCHER_VULKAN
    fmt.setRenderableType(QSurfaceFormat::OpenGL);
    fmt.setVersion(2, 1);
#else
    fmt.setRenderableType(QSurfaceFormat::OpenGLES);
    fmt.setVersion(3, 0);
#endif
    fmt.setRedBufferSize(8);
    fmt.setGreenBufferSize(8);
    fmt.setBlueBufferSize(8);
    fmt.setAlphaBufferSize(8);
    fmt.setDepthBufferSize(0);
    fmt.setStencilBufferSize(0);
    fmt.setSwapInterval(1);
    QSurfaceFormat::setDefaultFormat(fmt);

    QApplication app(argc, argv);
    app.setApplicationName(QStringLiteral("podish-launcher"));
    app.setDesktopFileName(QStringLiteral("podish-launcher"));
    app.setApplicationDisplayName(QStringLiteral("Podish Desktop"));

    if (QIcon::themeName().isEmpty()) {
        QIcon::setThemeName(QStringLiteral("breeze"));
    }

    const bool touchMode = qEnvironmentVariableIntValue("PODISH_TOUCH_MODE") != 0;
    GlLauncherWindow win(touchMode);
    win.showMaximized();
    return app.exec();
}
