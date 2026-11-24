#include <QApplication>
#include <QColor>
#include <QComboBox>
#include <QDateTime>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QFont>
#include <QFrame>
#include <QGridLayout>
#include <QHBoxLayout>
#include <QIcon>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLabel>
#include <QLineEdit>
#include <QLinearGradient>
#include <QMap>
#include <QPaintEvent>
#include <QPainter>
#include <QPainterPath>
#include <QPixmap>
#include <QPointer>
#include <QProcess>
#include <QProcessEnvironment>
#include <QPushButton>
#include <QResizeEvent>
#include <QSaveFile>
#include <QScrollArea>
#include <QScroller>
#include <QScrollerProperties>
#include <QSet>
#include <QStandardPaths>
#include <QTabWidget>
#include <QTextStream>
#include <QTimer>
#include <QToolButton>
#include <QVBoxLayout>
#include <QWidget>

#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <string>

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
            QString type, name, genericName, comment, icon, exec, path;
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

class SettingsWindow : public QWidget {
public:
    SettingsWindow(const QList<AppEntry>& apps, bool mobileMode, QWidget* parent = nullptr)
        : QWidget(parent, Qt::Window)
        , m_apps(apps)
        , m_mobileMode(mobileMode) {
        setWindowTitle(QStringLiteral("Podish Settings"));
        setObjectName(QStringLiteral("SettingsRoot"));
        setAttribute(Qt::WA_DeleteOnClose, true);
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
                background: rgba(15, 23, 42, 0.88);
                border: 1px solid rgba(148, 163, 184, 0.32);
                border-radius: 10px;
                color: #f8fafc;
                padding: 6px 10px;
                min-height: 30px;
                font-size: 13px;
            }
            QComboBox QAbstractItemView {
                background: #1e293b;
                color: #f8fafc;
                selection-background-color: #0284c7;
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

        // Global DPI Card
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

        // Per-App DPI Section
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

        // Memory Limit Card
        auto* memCard = new QFrame(inner);
        memCard->setObjectName(QStringLiteral("CardFrame"));
        auto* memLayout = new QVBoxLayout(memCard);
        memLayout->setContentsMargins(14, 12, 14, 12);
        memLayout->setSpacing(8);
        auto* memTitle = new QLabel(QStringLiteral("Guest Memory Quota"), memCard);
        memTitle->setObjectName(QStringLiteral("SectionTitle"));
        m_memoryCombo = new QComboBox(memCard);
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

        // Block Cache Limit Card
        auto* cacheCard = new QFrame(inner);
        cacheCard->setObjectName(QStringLiteral("CardFrame"));
        auto* cacheLayout = new QVBoxLayout(cacheCard);
        cacheLayout->setContentsMargins(14, 12, 14, 12);
        cacheLayout->setSpacing(8);
        auto* cacheTitle = new QLabel(QStringLiteral("CPU Block Cache Limit"), cacheCard);
        cacheTitle->setObjectName(QStringLiteral("SectionTitle"));
        m_blockCacheCombo = new QComboBox(cacheCard);
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
            combo->setMinimumWidth(116);
            combo->setMaximumWidth(134);
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

class LauncherWindow : public QWidget {
public:
    explicit LauncherWindow(bool mobileMode, QWidget* parent = nullptr)
        : QWidget(parent)
        , m_mobileMode(mobileMode) {
        setWindowTitle(QStringLiteral("Podish Desktop"));
        setObjectName(QStringLiteral("LauncherRoot"));
        setAttribute(Qt::WA_TranslucentBackground, true);
        setAutoFillBackground(false);
        setMinimumSize(320, 420);

        buildUi();
        reloadApps();

        auto* clockTimer = new QTimer(this);
        connect(clockTimer, &QTimer::timeout, this, &LauncherWindow::updateStatus);
        clockTimer->start(10000);
        updateStatus();

        m_toastTimer = new QTimer(this);
        m_toastTimer->setSingleShot(true);
        connect(m_toastTimer, &QTimer::timeout, this, [this]() { m_toastLabel->hide(); });
    }

protected:
    void paintEvent(QPaintEvent* event) override {
        Q_UNUSED(event);
        QPainter p(this);
        p.setCompositionMode(QPainter::CompositionMode_Source);
        // Replace backing-store pixels cleanly so translucent Wayland repaints never ghost stale tiles.
        QLinearGradient scrim(0, 0, 0, height());
        scrim.setColorAt(0.0, QColor(7, 13, 28, m_mobileMode ? 36 : 44));
        scrim.setColorAt(0.45, QColor(9, 16, 32, m_mobileMode ? 24 : 34));
        scrim.setColorAt(1.0, QColor(6, 11, 24, m_mobileMode ? 82 : 76));
        p.fillRect(rect(), scrim);
    }

    void resizeEvent(QResizeEvent* event) override {
        QWidget::resizeEvent(event);
        relayoutGrid();
        positionToast();
    }

private:
    void buildUi() {
        auto* root = new QVBoxLayout(this);
        const int sideMargin = m_mobileMode ? 18 : 22;
        const int topMargin = m_mobileMode ? 16 : 20;
        root->setContentsMargins(sideMargin, topMargin, sideMargin, 12);
        root->setSpacing(12);

        // Plasma Mobile Hero Clock + Status Pill
        auto* heroBox = new QVBoxLayout();
        heroBox->setSpacing(6);
        heroBox->setAlignment(Qt::AlignHCenter);

        m_clockLabel = new QLabel(QStringLiteral("12:00"), this);
        m_clockLabel->setObjectName(QStringLiteral("HeroClock"));
        m_clockLabel->setAlignment(Qt::AlignCenter);
        heroBox->addWidget(m_clockLabel);

        auto* statusPill = new QFrame(this);
        statusPill->setObjectName(QStringLiteral("StatusPill"));
        auto* statusPillLayout = new QHBoxLayout(statusPill);
        statusPillLayout->setContentsMargins(14, 5, 14, 5);
        statusPillLayout->setSpacing(6);
        m_metaLabel = new QLabel(QStringLiteral("Plasma Mobile  •  Debian i386"), statusPill);
        m_metaLabel->setObjectName(QStringLiteral("MetaLabel"));
        m_metaLabel->setAlignment(Qt::AlignCenter);
        statusPillLayout->addWidget(m_metaLabel);
        heroBox->addWidget(statusPill, 0, Qt::AlignHCenter);

        root->addLayout(heroBox);

        // Full-width Frosted Search Bar + Refresh + Settings buttons
        auto* searchRow = new QHBoxLayout();
        searchRow->setSpacing(8);

        m_searchEdit = new QLineEdit(this);
        m_searchEdit->setObjectName(QStringLiteral("SearchBox"));
        m_searchEdit->setPlaceholderText(QStringLiteral("Search apps & tools..."));
        m_searchEdit->setClearButtonEnabled(true);
        m_searchEdit->setMinimumHeight(44);
        connect(m_searchEdit, &QLineEdit::textChanged, this, [this]() { rebuildFilteredGrid(); });
        searchRow->addWidget(m_searchEdit, 1);

        auto* refreshBtn = new QPushButton(QStringLiteral("↻"), this);
        refreshBtn->setObjectName(QStringLiteral("RefreshBtn"));
        refreshBtn->setToolTip(QStringLiteral("Refresh application list"));
        refreshBtn->setFixedSize(44, 44);
        refreshBtn->setCursor(Qt::PointingHandCursor);
        connect(refreshBtn, &QPushButton::clicked, this, [this]() {
            reloadApps();
            showToast(QStringLiteral("Applications refreshed"));
        });
        searchRow->addWidget(refreshBtn);

        auto* settingsBtn = new QPushButton(QStringLiteral("⚙"), this);
        settingsBtn->setObjectName(QStringLiteral("RefreshBtn"));
        settingsBtn->setToolTip(QStringLiteral("Podish Settings (DPI, Per-App Scale, VM)"));
        settingsBtn->setFixedSize(44, 44);
        settingsBtn->setCursor(Qt::PointingHandCursor);
        connect(settingsBtn, &QPushButton::clicked, this, [this]() { openSettingsWindow(); });
        searchRow->addWidget(settingsBtn);

        root->addLayout(searchRow);

        // Horizontally scrollable category filter pills
        auto* catScroll = new QScrollArea(this);
        catScroll->setObjectName(QStringLiteral("CategoryScroll"));
        catScroll->setWidgetResizable(true);
        catScroll->setHorizontalScrollBarPolicy(Qt::ScrollBarAlwaysOff);
        catScroll->setVerticalScrollBarPolicy(Qt::ScrollBarAlwaysOff);
        catScroll->setFixedHeight(42);
        catScroll->setFrameShape(QFrame::NoFrame);
        catScroll->viewport()->setAutoFillBackground(false);
        QScroller::grabGesture(catScroll->viewport(), QScroller::LeftMouseButtonGesture);

        auto* catContainer = new QWidget(catScroll);
        catContainer->setObjectName(QStringLiteral("Transparent"));
        auto* catLayout = new QHBoxLayout(catContainer);
        catLayout->setContentsMargins(0, 0, 0, 0);
        catLayout->setSpacing(8);

        const QStringList categories = {
            QStringLiteral("All"),       QStringLiteral("Favorites"), QStringLiteral("System"),
            QStringLiteral("Utilities"), QStringLiteral("Graphics"),  QStringLiteral("Development"),
            QStringLiteral("Settings"),
        };
        for (const QString& cat : categories) {
            auto* pill = new QPushButton(cat, catContainer);
            pill->setObjectName(QStringLiteral("CategoryPill"));
            pill->setCheckable(true);
            pill->setChecked(cat == m_activeCategory);
            pill->setMinimumHeight(36);
            pill->setCursor(Qt::PointingHandCursor);
            connect(pill, &QPushButton::clicked, this, [this, cat]() {
                m_activeCategory = cat;
                for (auto it = m_categoryButtons.begin(); it != m_categoryButtons.end(); ++it) {
                    it.value()->setChecked(it.key() == m_activeCategory);
                }
                rebuildFilteredGrid();
            });
            m_categoryButtons.insert(cat, pill);
            catLayout->addWidget(pill);
        }
        catLayout->addStretch(1);
        catScroll->setWidget(catContainer);
        root->addWidget(catScroll);

        // Scrollable Plasma Mobile frosted app grid
        m_gridScroll = new QScrollArea(this);
        m_gridScroll->setObjectName(QStringLiteral("GridScroll"));
        m_gridScroll->setWidgetResizable(true);
        m_gridScroll->setFrameShape(QFrame::NoFrame);
        m_gridScroll->setHorizontalScrollBarPolicy(Qt::ScrollBarAlwaysOff);
        m_gridScroll->viewport()->setAutoFillBackground(false);

        QScroller::grabGesture(m_gridScroll->viewport(), QScroller::LeftMouseButtonGesture);
        QScrollerProperties scrollerProps = QScroller::scroller(m_gridScroll->viewport())->scrollerProperties();
        scrollerProps.setScrollMetric(QScrollerProperties::DragStartDistance, 0.004);
        scrollerProps.setScrollMetric(QScrollerProperties::DecelerationFactor, 0.65);
        QScroller::scroller(m_gridScroll->viewport())->setScrollerProperties(scrollerProps);

        m_gridContainer = new QWidget(m_gridScroll);
        m_gridContainer->setObjectName(QStringLiteral("Transparent"));
        m_gridLayout = new QGridLayout(m_gridContainer);
        m_gridLayout->setContentsMargins(2, 4, 2, 16);
        m_gridLayout->setHorizontalSpacing(12);
        m_gridLayout->setVerticalSpacing(14);
        m_gridLayout->setAlignment(Qt::AlignTop | Qt::AlignHCenter);
        m_gridScroll->setWidget(m_gridContainer);
        root->addWidget(m_gridScroll, 1);

        // Plasma Mobile floating frosted quick-launch dock
        auto* dock = new QFrame(this);
        dock->setObjectName(QStringLiteral("BottomDock"));
        auto* dockScroll = new QScrollArea(dock);
        dockScroll->setObjectName(QStringLiteral("Transparent"));
        dockScroll->setWidgetResizable(true);
        dockScroll->setFrameShape(QFrame::NoFrame);
        dockScroll->setHorizontalScrollBarPolicy(Qt::ScrollBarAlwaysOff);
        dockScroll->setVerticalScrollBarPolicy(Qt::ScrollBarAlwaysOff);
        dockScroll->viewport()->setAutoFillBackground(false);
        QScroller::grabGesture(dockScroll->viewport(), QScroller::LeftMouseButtonGesture);

        auto* dockOuter = new QHBoxLayout(dock);
        dockOuter->setContentsMargins(10, 8, 10, 8);
        dockOuter->addWidget(dockScroll);

        auto* dockInner = new QWidget(dockScroll);
        dockInner->setObjectName(QStringLiteral("Transparent"));
        m_dockLayout = new QHBoxLayout(dockInner);
        m_dockLayout->setContentsMargins(0, 0, 0, 0);
        m_dockLayout->setSpacing(10);
        m_dockLayout->setAlignment(Qt::AlignCenter);
        dockScroll->setWidget(dockInner);
        dock->setFixedHeight(74);
        root->addWidget(dock);

        // Floating toast label
        m_toastLabel = new QLabel(this);
        m_toastLabel->setObjectName(QStringLiteral("ToastLabel"));
        m_toastLabel->setAlignment(Qt::AlignCenter);
        m_toastLabel->hide();

        setStyleSheet(QStringLiteral(R"(
            #LauncherRoot {
                background: transparent;
                color: #f8fafc;
            }
            #Transparent {
                background: transparent;
            }
            #HeroClock {
                color: #ffffff;
                font-size: 46px;
                font-weight: 300;
                letter-spacing: 1px;
            }
            #StatusPill {
                background: rgba(12, 20, 38, 0.44);
                border: 1px solid rgba(255, 255, 255, 0.16);
                border-radius: 14px;
            }
            #MetaLabel {
                color: #e2e8f0;
                font-size: 12px;
                font-weight: 500;
            }
            #SearchBox {
                background: rgba(15, 23, 42, 0.52);
                border: 1px solid rgba(255, 255, 255, 0.20);
                border-radius: 22px;
                padding: 0 18px;
                color: #f8fafc;
                font-size: 15px;
                selection-background-color: #3daee9;
            }
            #SearchBox:focus {
                border: 1px solid rgba(61, 174, 233, 0.85);
                background: rgba(15, 23, 42, 0.78);
            }
            #RefreshBtn {
                background: rgba(15, 23, 42, 0.52);
                border: 1px solid rgba(255, 255, 255, 0.20);
                border-radius: 22px;
                color: #e2e8f0;
                font-size: 19px;
                font-weight: 600;
            }
            #RefreshBtn:pressed {
                background: rgba(61, 174, 233, 0.42);
                border: 1px solid #3daee9;
            }
            #CategoryScroll, #GridScroll {
                background: transparent;
            }
            #CategoryPill {
                background: rgba(15, 23, 42, 0.42);
                border: 1px solid rgba(255, 255, 255, 0.15);
                border-radius: 18px;
                color: #e2e8f0;
                padding: 0 16px;
                font-size: 13px;
                font-weight: 600;
            }
            #CategoryPill:checked {
                background: rgba(61, 174, 233, 0.86);
                border: 1px solid rgba(147, 217, 255, 0.90);
                color: #ffffff;
            }
            #AppTile {
                background: rgba(15, 23, 42, 0.36);
                border: 1px solid rgba(255, 255, 255, 0.13);
                border-radius: 22px;
                color: #f8fafc;
                padding: 10px 6px 8px 6px;
                font-size: 12px;
                font-weight: 600;
            }
            #AppTile:hover {
                background: rgba(30, 41, 59, 0.56);
                border: 1px solid rgba(61, 174, 233, 0.55);
            }
            #AppTile:pressed {
                background: rgba(61, 174, 233, 0.38);
                border: 1px solid #3daee9;
            }
            #BottomDock {
                background: rgba(12, 20, 38, 0.56);
                border: 1px solid rgba(255, 255, 255, 0.18);
                border-radius: 24px;
            }
            #DockButton {
                background: rgba(255, 255, 255, 0.08);
                border: 1px solid rgba(255, 255, 255, 0.12);
                border-radius: 16px;
                color: #f8fafc;
                padding: 6px 12px;
                font-size: 12px;
                font-weight: 600;
            }
            #DockButton:hover {
                background: rgba(61, 174, 233, 0.28);
                border: 1px solid rgba(61, 174, 233, 0.65);
            }
            #DockButton:pressed {
                background: rgba(61, 174, 233, 0.48);
            }
            #ToastLabel {
                background: rgba(12, 20, 38, 0.92);
                border: 1px solid rgba(61, 174, 233, 0.65);
                border-radius: 16px;
                color: #f8fafc;
                padding: 10px 20px;
                font-size: 13px;
                font-weight: 600;
            }
            QScrollBar:vertical {
                background: transparent;
                width: 6px;
                margin: 4px 0;
            }
            QScrollBar::handle:vertical {
                background: rgba(255, 255, 255, 0.28);
                border-radius: 3px;
                min-height: 28px;
            }
            QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {
                height: 0px;
            }
        )"));
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

    void updateStatus() {
        const QDateTime now = QDateTime::currentDateTime();
        m_clockLabel->setText(now.toString(QStringLiteral("HH:mm")));
        m_metaLabel->setText(now.toString(QStringLiteral("ddd, MMM d")) + QStringLiteral("  •  ") +
                             readMemorySummary());
    }

    void reloadApps() {
        m_allApps = scanDesktopEntries();
        rebuildDock();
        rebuildFilteredGrid();
        if (m_settingsWindow) {
            m_settingsWindow->refreshApps(m_allApps);
        }
    }

    void rebuildDock() {
        while (QLayoutItem* item = m_dockLayout->takeAt(0)) {
            if (QWidget* w = item->widget())
                w->deleteLater();
            delete item;
        }

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
                if (QStandardPaths::findExecutable(t.fallbackExec).isEmpty())
                    continue;
                entry.id = t.id;
                entry.name = t.label;
                entry.execLine = t.fallbackExec;
                entry.iconName = t.fallbackIcon;
            }

            auto* btn = new QToolButton(this);
            btn->setObjectName(QStringLiteral("DockButton"));
            btn->setToolButtonStyle(m_mobileMode ? Qt::ToolButtonIconOnly : Qt::ToolButtonTextBesideIcon);
            btn->setIcon(resolveAppIcon(entry.iconName, t.label));
            btn->setIconSize(m_mobileMode ? QSize(32, 32) : QSize(26, 26));
            btn->setText(t.label);
            btn->setToolTip(t.label);
            if (m_mobileMode) {
                btn->setFixedSize(56, 54);
            } else {
                btn->setFixedHeight(52);
            }
            btn->setCursor(Qt::PointingHandCursor);
            connect(btn, &QToolButton::clicked, this, [this, entry]() { launchApp(entry); });
            m_dockLayout->addWidget(btn);
        }
    }

    void rebuildFilteredGrid() {
        for (QWidget* w : m_tileWidgets) {
            m_gridLayout->removeWidget(w);
            w->deleteLater();
        }
        m_tileWidgets.clear();

        const QString query = m_searchEdit->text().trimmed();
        QList<AppEntry> filtered;
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
            filtered.append(app);
        }

        if (filtered.isEmpty()) {
            auto* empty = new QLabel(QStringLiteral("No matching applications"), m_gridContainer);
            empty->setAlignment(Qt::AlignCenter);
            empty->setStyleSheet(QStringLiteral("color: #cbd5e1; font-size: 16px; padding: 48px;"));
            m_tileWidgets.append(empty);
        } else {
            for (const AppEntry& app : filtered) {
                auto* tile = new QToolButton(m_gridContainer);
                tile->setObjectName(QStringLiteral("AppTile"));
                tile->setToolButtonStyle(Qt::ToolButtonTextUnderIcon);
                tile->setIcon(resolveAppIcon(app.iconName, app.name));
                tile->setIconSize(QSize(52, 52));
                tile->setText(app.name);
                tile->setToolTip(app.comment.isEmpty() ? app.name : app.comment);
                tile->setFixedSize(122, 122);
                tile->setCursor(Qt::PointingHandCursor);
                connect(tile, &QToolButton::clicked, this, [this, app]() { launchApp(app); });
                m_tileWidgets.append(tile);
            }
        }

        m_lastColumns = -1;
        relayoutGrid();
    }

    void relayoutGrid() {
        if (!m_gridScroll || !m_gridLayout)
            return;
        const int availWidth = std::max(280, m_gridScroll->viewport()->width() - 8);
        const int targetCell = m_mobileMode ? 114 : 134;
        const int cols = std::max(3, availWidth / targetCell);
        const int tileW = std::clamp((availWidth - (cols - 1) * 12) / cols, 86, 140);
        const int tileH = std::clamp(tileW + 4, 96, 134);

        if (cols == m_lastColumns && tileW == m_lastTileWidth && m_gridLayout->count() == m_tileWidgets.size()) {
            return;
        }
        m_lastColumns = cols;
        m_lastTileWidth = tileW;

        while (m_gridLayout->count() > 0) {
            m_gridLayout->takeAt(0);
        }

        if (m_tileWidgets.size() == 1 && qobject_cast<QLabel*>(m_tileWidgets.first())) {
            m_gridLayout->addWidget(m_tileWidgets.first(), 0, 0, 1, cols, Qt::AlignCenter);
            return;
        }

        for (int i = 0; i < m_tileWidgets.size(); ++i) {
            m_tileWidgets[i]->setFixedSize(tileW, tileH);
            m_gridLayout->addWidget(m_tileWidgets[i], i / cols, i % cols, Qt::AlignCenter);
        }
    }

    void launchApp(const AppEntry& app) {
        QStringList args = splitDesktopExec(app.execLine);
        if (args.isEmpty()) {
            showToast(QStringLiteral("Cannot launch ") + app.name);
            return;
        }

        QString program = args.takeFirst();
        if (app.terminal) {
            QStringList termArgs;
            termArgs << QStringLiteral("-e") << program;
            termArgs.append(args);
            program = QStringLiteral("qterminal");
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
        proc.setProcessEnvironment(QProcessEnvironment::systemEnvironment());
        qint64 pid = 0;
        if (proc.startDetached(&pid)) {
            showToast(QStringLiteral("Launching %1...").arg(app.name));
        } else {
            showToast(QStringLiteral("Failed to start %1").arg(app.name));
        }
    }

    void showToast(const QString& text) {
        m_toastLabel->setText(text);
        m_toastLabel->adjustSize();
        positionToast();
        m_toastLabel->show();
        m_toastLabel->raise();
        m_toastTimer->start(2200);
    }

    void positionToast() {
        if (!m_toastLabel)
            return;
        const int x = (width() - m_toastLabel->width()) / 2;
        const int y = std::max(16, height() - m_toastLabel->height() - 102);
        m_toastLabel->move(x, y);
    }

    bool m_mobileMode = false;
    QLabel* m_clockLabel = nullptr;
    QLabel* m_metaLabel = nullptr;
    QLineEdit* m_searchEdit = nullptr;
    QScrollArea* m_gridScroll = nullptr;
    QWidget* m_gridContainer = nullptr;
    QGridLayout* m_gridLayout = nullptr;
    QHBoxLayout* m_dockLayout = nullptr;
    QLabel* m_toastLabel = nullptr;
    QTimer* m_toastTimer = nullptr;
    QPointer<SettingsWindow> m_settingsWindow;

    QString m_activeCategory = QStringLiteral("All");
    QMap<QString, QPushButton*> m_categoryButtons;
    QList<AppEntry> m_allApps;
    QList<QWidget*> m_tileWidgets;
    int m_lastColumns = -1;
    int m_lastTileWidth = -1;
};

} // namespace

int main(int argc, char* argv[]) {
    QApplication app(argc, argv);
    app.setApplicationName(QStringLiteral("podish-launcher"));
    app.setDesktopFileName(QStringLiteral("podish-launcher"));
    app.setApplicationDisplayName(QStringLiteral("Podish Desktop"));

    if (QIcon::themeName().isEmpty()) {
        QIcon::setThemeName(QStringLiteral("breeze"));
    }

    const bool touchMode = qEnvironmentVariableIntValue("PODISH_TOUCH_MODE") != 0;
    LauncherWindow win(touchMode);
    if (touchMode) {
        win.showMaximized();
    } else {
        win.resize(1040, 680);
        win.show();
    }
    return app.exec();
}
