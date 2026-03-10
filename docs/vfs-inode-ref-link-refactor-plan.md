# VFS Inode/Dentry 统一引用模型改造方案（RefCount + LinkCount + DentryRefCount）

## 文档状态
- Owner: Fiberish Core Maintainers
- Last Updated: 2026-03-10
- Status: Final v1.0
- Scope: `Fiberish.Core/VFS` + `SyscallHandlers` + 各具体文件系统实现

## 1. 背景与目标

当前实现里，`Inode` 基类已有 `RefCount`，但多处生命周期语义仍然分散在各文件系统中（例如 HostFS/SilkFS 的额外计数与清理逻辑）。  
目标是对齐 Linux 的核心模型，并统一收敛 inode/dentry 生命周期语义：

- `RefCount` 对齐 Linux `i_count`（内核对象被引用次数）
- `LinkCount` 对齐 Linux `i_nlink`（命名空间中硬链接数量）
- `DentryRefCount` 对齐 Linux `d_count`（dcache 对象内存引用次数）

同时引入 Linux 风格的 cache 分层语义：

- `inode cache` / `dcache` 可在 `RefCount==0` / `DentryRefCount==0` 时被 shrinker 回收
- cache 回收不等于后端删除
- 仅在 `RefCount==0 && LinkCount==0` 时允许最终删除后端对象

最终判定规则：

- `RefCount > 0`：inode 必须存活（不可销毁）
- `LinkCount > 0`：对象在命名空间可达（即使未打开，也不删除后端对象）
- `RefCount == 0`：inode 可进入“可回收 cache”状态（允许内存回收）
- 仅当 `RefCount == 0 && LinkCount == 0`：允许最终删除后端对象

## 2. 设计原则

1. 单一真相源（Single Source of Truth）  
   inode 生命周期只由 `Inode` 基类管理，FS 不再自定义“并行生命周期计数器”。

2. 语义解耦  
   `RefCount`（运行时引用）和 `LinkCount`（命名空间可达性）严格分离，不再使用 `Dentries.Count` 近似 `LinkCount`。

3. 显式来源  
   所有引用增减必须带来源（open/mmap/path-pin/internal-pin），用于审计与测试断言。

4. 文件系统只负责命名空间事件  
   FS 只在 `create/link/unlink/rename/...` 等 namespace 事件中改动 `LinkCount`，不直接决定 inode 最终生死。

5. 回收路径统一  
   回收语义统一由基类驱动，区分：
   - cache evict（仅内存对象回收）
   - final delete（后端对象最终删除）
   FS 只实现后端清理细节。

6. 不依赖“缓存结构偶然性”  
   dentry cache、children 字典、path cache 都不能作为生命周期判据。

7. dentry/inode 双对象模型  
   `DentryRefCount` 与 `Inode.RefCount/LinkCount` 分工明确，禁止混用。

8. Shrinker 驱动回收  
   dcache/inode cache 的回收必须由统一 shrinker 路径驱动，并且不改变命名空间 link 语义。

## 3. 核心数据模型（拟）

在 `Inode` 基类新增/收敛：

- `int RefCount`：运行时引用计数
- `int LinkCount`：硬链接计数
- `bool IsCacheEvicted`：inode 内存对象是否已从 cache 回收
- `bool IsFinalized`：后端对象是否已最终删除（防重复）

在 `Dentry`（或等价 lockref 结构）新增/收敛：

- `int DentryRefCount`：dentry 内存引用计数（对齐 `d_count`）
- `bool IsHashed`：是否在 dcache 哈希中
- `bool IsNegative`：负 dentry（lookup miss 缓存）

在 `SuperBlock` 新增/收敛：

- `active inode set`：有活动引用或近期活跃对象
- `unused inode LRU`：`RefCount==0` 的 cache inode 候选队列
- `dcache LRU`：`DentryRefCount==0` 的 dentry 候选队列

建议 API：

- `AcquireRef(InodeRefKind kind, string? reason = null)`
- `ReleaseRef(InodeRefKind kind, string? reason = null)`
- `IncLink(string? reason = null)`
- `DecLink(string? reason = null)`
- `TryEvictCache()`：当 `RefCount == 0` 且 shrinker/drop_caches 驱动时触发 cache evict
- `TryFinalizeDelete()`：当 `RefCount == 0 && LinkCount == 0` 时触发最终删除
- `protected virtual OnEvictCache()`：仅释放内存态资源（页缓存、内存索引）
- `protected virtual OnFinalizeDelete()`：后端最终删除与超级块索引移除

`InodeRefKind`（建议）：

- `FileOpen`
- `FileMmap`
- `PathPin`（cwd/root/procRoot 等）
- `KernelInternal`

说明：  
- `RefByKind` 分桶计数可选（调试用途）。  
- 生命周期判据禁止使用 `Dentries.Count` 近似推导。  
- dentry 与 inode 的关系是“dentry 引用 inode”，不是“dentry 数量决定 link 数”。

## 4. 生命周期状态机（统一）

1. 创建 inode：`RefCount=0`，`LinkCount=0`
2. 首次落名（create/mkdir/mknod/symlink）：`LinkCount=1`
3. open/mmap/path-pin：`RefCount += 1`
4. unlink/rmdir/rename-overwrite/link：按 namespace 语义更新 `LinkCount`
5. close/munmap/path-unpin：`RefCount -= 1`
6. 每次 `ReleaseRef/DecLink` 后执行两阶段判定：
   - `RefCount == 0`：进入 unused inode 集合，等待 shrinker/drop_caches 执行 `TryEvictCache`
   - `LinkCount == 0 && RefCount > 0`：orphan（已摘链但仍被打开/映射）
   - `LinkCount == 0 && RefCount == 0`：触发 `TryFinalizeDelete`（最终删除，随后强制 evict）

补充（dentry 生命周期）：

1. dentry 创建并入 hash：`DentryRefCount` 初始化并受 path/file 持有影响
2. `DentryRefCount == 0`：进入 dcache LRU，可被 shrinker 回收
3. 回收 dentry 时，若为正 dentry，需要对其关联 inode 做对称 `iput/ReleaseRef(KernelInternal)`

## 5. VFS 公共层改造

### 5.1 Dentry 与 LinkCount 解耦

- `Dentry` 构造/`Instantiate` 不再隐式代表 link 变化。
- `Dentries` 列表仅作为“指向该 inode 的缓存 dentry 反向索引”，不是链接数。
- 所有从目录树剥离 dentry 的路径必须对称地 `inode.Dentries.Remove(dentry)`，但不自动改 `LinkCount`。
- 引入真实 `DentryRefCount`（`d_count` 语义），不再以 no-op 表达 dentry 生命周期。

### 5.2 LinuxFile / mmap

- `LinuxFile` 构造：统一 `AcquireRef(FileOpen|FileMmap)`。
- `LinuxFile.Close`：统一 `ReleaseRef(...)`。
- `mmap fd close` 语义继续通过 `MmapHold` 保持 inode 引用，直到 VMA 回收。

### 5.3 Path pin

- `SyscallManager.Root/CurrentWorkingDirectory/ProcessRoot` 持有完整 path pin：
  - `mount.Get/Put`（`vfsmount` 生命周期）
  - `dentry.Get/Put`（`dentry` 生命周期）
  - `inode AcquireRef/ReleaseRef(PathPin)`（路径引用来源审计）
- `chdir/chroot/clone/exit` 保证 pin 的增减严格对称。
- `clone(CLONE_FS)` 共享同一 `fs_struct`（`root/cwd/procroot` 共享可见）。
- `clone(!CLONE_FS)` 复制独立 `fs_struct`（后续 `chdir/chroot` 互不影响）。

### 5.4 SuperBlock inode 跟踪

- 所有 FS 创建 inode 必须注册到 `SuperBlock` 的统一 inode 集合（用于回收扫描与 debug）。
- `HasActiveInodes()` 改为基于统一引用模型，不再依赖 `RefCount > Dentries.Count` 这类近似逻辑。
- 增加 `unused inode LRU` 与 `dcache LRU` 管理结构，为 shrinker 提供统一候选集。

### 5.5 通用 Inode/Dentry 当前缺口与语义澄清

#### 5.5.1 当前缺口（需要先收敛）

1. 缺少 `LinkCount` 作为一等字段  
   - 当前大量逻辑仍把 `Dentries.Count` 近似当作链接数量，语义不稳定。

2. `RefCount` 来源不透明  
   - `Get/Put` 没有来源维度（open/mmap/path-pin/internal），调试与审计困难。

3. `Release` 命名语义冲突  
   - 既有 `Release(LinuxFile)`（文件关闭回调）又有 `protected Release()`（引用归零回调），易误用。

4. `Dentry` 引用计数语义不完整  
   - 缺少 Linux 对齐的 `d_count` 语义（当前为 no-op 或伪计数），无法正确表达 dcache LRU 回收。

5. `Dentry <-> Inode` 绑定关系缺少原子 API  
   - 现在大量手工操作 `inode.Dentries.Add/Remove + inode.Get/Put + parent.Children`，容易不对称。

6. `st_nlink` 报告不一致  
   - `stat64` 有路径固定写 1；`statx` 又走 `Dentries.Count`，与 Linux 语义不一致。

7. inode 跟踪注册未强制  
   - `SuperBlock.Inodes/AllInodes` 依赖各 FS 手动维护，导致 busy 判定与回收扫描可能失真。

8. dcache 语义未明确定义  
   - 负 dentry、失效策略、缓存 dentry 与命名空间 dentry 的边界不清晰。

9. shrinker 语义缺口  
   - 尚未严格区分“cache evict”与“final delete”，导致 `RefCount==0` 的 inode cache 回收与后端删除边界模糊。

#### 5.5.2 建议的通用不变量（实现必须满足）

- `LinkCount >= 0`，`RefCount >= 0`，禁止出现负计数。
- `DentryRefCount >= 0`，禁止出现 dentry 引用下溢。
- 仅 `LinkCount` 表示命名空间可达性；`Dentries.Count` 仅是缓存/别名索引。
- 所有 inode 绑定/解绑必须走统一入口，禁止散落手工改 `Dentries`。
- 所有 `RefCount` 增减必须可归因到 `InodeRefKind`。
- `stat/statx` 的 `nlink` 统一来自 `LinkCount`。
- cache 回收入口：`RefCount==0` -> `TryEvictCache`（不改 link，不删后端）。
- 最终删除入口：`RefCount==0 && LinkCount==0` -> `TryFinalizeDelete`。
- dentry shrink 回收必须对称释放 inode 绑定引用（正 dentry）。

#### 5.5.3 建议新增/重命名的基础 API

- Inode：
  - `AcquireRef(kind)` / `ReleaseRef(kind)`
  - `IncLink()` / `DecLink()`
  - `AttachAliasDentry(dentry)` / `DetachAliasDentry(dentry)`
  - `OnFileOpen(file)` / `OnFileClose(file)`（替代易混淆 `Open/Release(LinuxFile)` 命名）
  - `OnEvictCache()` / `OnFinalizeDelete()`（替代单一 `OnEvict` 语义）

- Dentry：
  - `BindInode(inode)` / `UnbindInode()`
  - `AttachToParent(parent)` / `DetachFromParent()`
  - 将当前 `_refCount` 明确为“path pin 引用”或移除，避免伪语义字段。

#### 5.5.4 与 rename/unlink/drop_caches 的边界约定

- `unlink/rmdir`：先 namespace 解绑，再 `DecLink`，不直接影响现存 open/mmap 引用。
- `rename`：非覆盖不改 link；覆盖目标只对被覆盖 inode `DecLink`。
- `drop_caches`：只能清理 dcache/无引用 inode cache，不得改变 `LinkCount`。

### 5.6 Shrinker 与 Cache 回收语义（新增）

- 统一引入 `VfsShrinker`（或等价模块）：
  - shrink dcache：仅处理 `DentryRefCount==0` 的候选 dentry。
  - shrink inode cache：仅处理 `RefCount==0` 的候选 inode。
- inode shrink 规则：
  - `RefCount==0 && LinkCount>0`：允许回收内存对象（后续可 `iget` 重建），禁止删除后端对象。
  - `RefCount==0 && LinkCount==0`：允许触发最终删除。
- dentry shrink 规则：
  - 正 dentry 回收时必须执行 `UnbindInode` 对称路径。
  - 负 dentry 可回收但不得影响任何 `LinkCount`/后端状态。
- `drop_caches` 语义对齐 Linux：
  - mode=1：page cache
  - mode=2：dentry+inode cache（仅回收可回收 cache，不做业务删除）
  - mode=3：两者都做

## 6. 各文件系统处理方案

### 6.1 IndexedMemoryFs / Tmpfs（基线实现）

- 作为“标准实现模板”：
  - `Create/Mkdir/Mknod/Symlink`：新 inode `LinkCount=1`
  - `Link`：目标 inode `IncLink`
  - `Unlink/Rmdir`：目标 inode `DecLink`
  - `Rename`：
    - 普通重命名：link 数不变
    - 覆盖目标：被覆盖 inode `DecLink`
- 删除 `_openCount` 等私有生命周期判据，改由基类 `RefCount` 驱动 orphan/evict。

### 6.2 SilkFS（基于 IndexedMemoryFs）

- 目标：SQLite 扮演“磁盘 inode 表 + dentry 表”，内存 inode 扮演“in-core inode cache”，语义对齐 Linux `iget/iput/evict`。

#### 6.2.1 当前行为与 Linux 语义差距（现状分析）

- `inodes.nlink` 当前未作为真相使用，`UpsertInode` 多处固定写 `nlink=1`，与 hardlink/rename-overwrite 语义不一致。
- 历史上 orphan 判定曾依赖 `CountDentryRefs + 运行时_open/mmap计数`；该遗留路径已移除并统一到 `RefCount/LinkCount` 模型。
- 崩溃恢复路径缺少“nlink=0 orphan inode 清理”流程，可能留下 SQLite 中可见但命名空间不可达的僵尸 inode。
- 挂载时倾向于全量重建 inode 对象，尚未形成按 ino 的 `iget`/缓存淘汰模型。

#### 6.2.2 Linux 对齐后的 SilkFS 映射模型

- `inodes` 表是持久层 inode 真相：
  - `ino`：稳定 inode 身份
  - `nlink`：持久硬链接计数（必须与 namespace 事务一致）
  - `mode/uid/gid/rdev/size/timestamps`：持久属性
- `nlink` 语义按 Linux 区分：
  - 普通文件/符号链接/设备节点：`nlink = 指向该 inode 的目录项数`
  - 目录：`nlink = 2 + 子目录数量`（`.` 与每个子目录的 `..` 语义）
- `dentries(parent_ino, name) -> ino` 是命名空间真相。
- 内存层维护 `ino -> SilkInode` 的 inode cache（可被回收），不再要求“所有 inode 常驻内存”。
- inode 被内存回收（evict）时：
  - 若 `LinkCount > 0`：只释放内存对象与页缓存，SQLite inode 行保留。
  - 若 `LinkCount == 0 && RefCount == 0`：删除 SQLite inode 行及关联对象绑定/live 数据。

#### 6.2.3 SQLite 事务与一致性规则（必须）

- `create/mkdir/mknod/symlink`：同一事务内完成
  - 插入 `inodes`（`nlink` 初值）
  - 插入 `dentries`
  - 更新父目录时间戳/必要字段
- `link`：同一事务内 `insert dentry + target.nlink++`
- `unlink/rmdir`：同一事务内 `delete dentry + target.nlink--`
- `rename`：
  - 不覆盖：仅移动 dentry，源 inode `nlink` 不变
  - 覆盖：被覆盖 inode `nlink--`
- `nlink` 更新不能依赖内存对象推导，必须在 SQLite 事务里原子更新。

#### 6.2.4 orphan 语义（对齐 Linux）

- 当 `nlink` 降到 0 但 `RefCount > 0`（打开/mmap/pin）：
  - inode 保留为 orphan，数据可继续通过 fd/mmap 访问。
  - 此时 inode 对应 dentry 可不存在，`dentries` 不可达是预期行为。
- 当最后一个 `RefCount` 释放：
  - 触发最终删除：`DELETE inodes`（级联清理 xattrs/inode_objects）+ 对象 refcount 递减 + live data 删除。

#### 6.2.5 崩溃恢复与挂载恢复

- 挂载恢复时必须执行 orphan 回收：
  - 扫描 `nlink=0` 的 inode，执行最终清理（进程已消失，运行时引用视为 0）。
  - 重建 object refcount（现有逻辑可保留）后再做 orphan 清理，避免对象泄漏。
- 恢复后要求：
  - `dentries` 可达集合与 `inodes.nlink` 一致
  - 无 `nlink=0` 且仍绑定命名空间的异常条目

#### 6.2.6 建议的 schema/约束增强

- `dentries.parent_ino` 增加外键到 `inodes(ino)`，防止悬挂父节点。
- 为 `inodes(nlink)`、`dentries(ino)` 建索引，支撑 orphan 回收与 fsck 校验。
- 可选增加 `orphan_inodes(ino)` 显式表（类似 ext4 orphan list）以简化恢复扫描与调试审计。

#### 6.2.7 实施要求

- 去除 `_openRefCount/_mmapRefCount` 对“是否删除 SQLite inode”的主判据角色，收敛到基类 `RefCount/LinkCount`。
- `CountDentryRefs` 已从实现中移除；生命周期判据仅允许使用 `RefCount/LinkCount`。
- `OnFinalizeDelete` 中统一执行 SQLite 最终清理与对象/live 文件回收。

### 6.3 HostFS（重点）

- inode 身份由 `(st_dev, st_ino)` 建模，不能以路径字符串作为 inode 身份。
- 支持同 inode 多路径别名（hard link）：
  - `link` 增加 `LinkCount`
  - `unlink` 减少 `LinkCount`
  - `rename` 不改变 source inode 的 `LinkCount`
- `HostPath` 仅作为“某个可用路径提示”，不能作为 inode 身份真相。
- 在 `OnEvictCache/OnFinalizeDelete` 执行：
  - 释放 mapped page backend
  - 清理 hostfs inode cache 索引
  - 若 `LinkCount==0` 且需要删除后端文件，则执行真实 host 删除（按实际语义触发）

#### 6.3.1 跨平台“底层数据”能力边界

- Linux：
  - 可稳定获取对象身份（`st_dev + st_ino`）与文件内容。
  - 可通过挂载信息识别 mount 边界，语义最接近 Linux VFS。
- macOS：
  - 可获取对象身份（`st_dev + st_ino`）与文件内容。
  - 大体可按 Unix 语义建模，但与 Linux 在挂载/权限细节上存在差异。
- Windows：
  - 需用 `VolumeId + FileId` 建模对象身份，不可直接套用 POSIX inode 语义。
  - reparse point（symlink/junction/mount point）与 ACL 语义与 Linux 差异大。
- WASI：
  - 仅有 capability/preopen 视图，通常无法完整获取 host mount/inode 细节。
  - 需降级为受限能力模型，不承诺完整 Linux mount 语义。

#### 6.3.2 HostFS 与“奇怪对象/挂载点”处理原则

- 必须先识别对象类型：regular/dir/symlink/socket/device/fifo/reparse。
- 必须识别并显式处理 mount 边界（Unix `st_dev` 变化，Windows `VolumeId` 变化）。
- 对不可安全模拟或平台不支持对象，返回明确错误（`ENOTSUP/EPERM`），禁止静默降级为普通文件。
- `LinkCount` 仅按“可见命名空间链接关系”变化，不受 cache/path 结构偶然性影响。

#### 6.3.3 备选方案

1. 方案 A：单挂载域严格隔离（默认推荐）
   - 仅允许同一 `fs_id` 内解析和访问，遇到 host 挂载点即边界终止。
   - 优点：语义稳定、安全边界清晰、最易对齐 `RefCount/LinkCount`。
   - 缺点：能力保守，跨挂载访问受限。

2. 方案 B：多挂载透传 + 统一对象身份层
   - 允许跨挂载解析，统一抽象 `ObjectIdentity(fs_id, object_id)`。
   - 优点：功能覆盖最广。
   - 缺点：实现复杂，Windows/WASI 适配成本高。

3. 方案 C：快照/索引化 HostFS（受控同步）
   - 运行前将 host 树导入受控索引，运行时基于索引+增量同步。
   - 优点：跨平台行为最可控，语义一致性最好。
   - 缺点：实时性与实现复杂度折中较差。

4. 方案 D：WASI 专用降级后端
   - 基于 preopen 能力，仅实现受限文件语义（不承诺完整 mount/hardlink/special node）。
   - 优点：在 WASI 可落地、行为可预测。
   - 缺点：与 Linux 完整语义存在差距。

#### 6.3.4 推荐落地路径

- 默认采用 A（主机平台）+ D（WASI）。
- 先建立统一后端抽象，再决定是否演进到 B：
  - `IHostFsBackend`
  - `IObjectIdentityProvider`
  - `IMountBoundaryPolicy`
  - `ISpecialNodePolicy`
- 在未完成统一抽象前，禁止业务层散落平台分支判断。

### 6.4 LayerFS（只读）

- 读取 layer 元数据时初始化 `LinkCount`（建议至少 1；目录按元数据真实值）。
- 只读 FS 不会改 link；`RefCount` 随 open/mmap/path-pin 变化。
- 允许在 `RefCount==0` 时作为缓存回收（若未来引入 inode LRU）。

### 6.5 OverlayFS（包装层）

- Overlay inode 是视图层对象，必须与 upper/lower inode 生命周期边界清晰：
  - overlay namespace 操作更新“overlay 视角”的 `LinkCount`
  - 实际持久变化仍委托 upper FS；lower 只读
- copy-up 场景：
  - 新 upper inode 的 `LinkCount` 从 1 开始（或按目标目录事件计算）
  - 旧 lower inode 不因 copy-up 直接改 link
- whiteout/opaque 不应绕过 `LinkCount` 语义，应映射为 namespace 可达性变化。

### 6.6 ProcFS / DevPts / Anon inode（虚拟文件）

- 这类 inode 没有持久后端，可采用 `LinkCount=1`（可达时）或 `0`（纯匿名）策略，但规则必须一致并文档化：
  - 路径可见节点（如 `/proc/<pid>/status`）：可设 `LinkCount=1`
  - 纯匿名节点（epoll/timerfd/socket anon inode）：`LinkCount=0`，仅靠 `RefCount` 存活
- 最终回收统一走基类，不再各处手工“顺便清理”。

## 7. syscall 语义对齐清单

- `open/close`：只影响 `RefCount`
- `mmap/munmap`：只影响 `RefCount`
- `link/unlink`：只影响 `LinkCount`
- `rename`：
  - 移动不覆盖：link 不变
  - 覆盖目标：目标 `LinkCount--`
- `chdir/chroot/exit`：只影响 `PathPin` 引用
- `clone(CLONE_FS)`：共享同一组 `root/cwd/procroot`
- `clone(!CLONE_FS)`：复制一组独立 `root/cwd/procroot`

## 8. 测试计划（必须先补齐）

### 8.1 通用不变量测试

- 任何时刻不允许出现负计数（`RefCount<0` 或 `LinkCount<0`）
- 任何时刻不允许出现负 dentry 引用计数（`DentryRefCount<0`）
- `unlink + open fd`：`LinkCount==0 && RefCount>0`，文件可读写
- `unlink + mmap`：关闭 fd 后仍可访问映射
- 最后一个 `close/munmap` 后触发 `OnFinalizeDelete`

### 8.2 FS 维度测试

- HostFS：
  - hard link 别名 + rename + unlink 组合
  - path cache 回收不影响 link/ref 判定
  - 跨 mount 边界访问策略（同 fs_id 允许，跨 fs_id 按策略拒绝或透传）
  - symlink/reparse/mount-point 特殊节点行为与错误码符合策略定义
- SilkFS：
  - hardlink/link/unlink/rename-overwrite 后 `inodes.nlink` 与预期一致
  - 目录 `nlink` 在 mkdir/rmdir 后满足 `2 + 子目录数量`
  - `nlink=0 && ref>0` orphan 在 close/munmap 前可继续 I/O
  - 挂载恢复后 `nlink=0` 僵尸 inode 被清理
  - 内存 inode 被回收后可通过 `iget(ino)` 从 SQLite 无损重建
- OverlayFS：
  - lower-only copy-up 后 link/ref 行为正确
  - whiteout/rename overwrite 不丢计数
- LayerFS/ProcFS：
  - 只读或动态 inode 不出现泄漏/过早回收

### 8.3 umount/drop_caches 回归

- `umount` busy 判定由统一模型驱动
- `drop_caches` 只能回收“无活动引用”的缓存对象，不能破坏已引用 inode
- `drop_caches(mode=2)` 后可通过路径访问触发 `iget`，并恢复被回收 inode 的可见语义
- `clone(CLONE_FS)` 与非 `CLONE_FS` 两条路径下 `chdir/chroot` 可见性符合预期

## 9. 分阶段实施

### Phase A0: 基线冻结与可观测性（先做）

- 在现有 `Get/Put`、`Dentries.Add/Remove`、`stat(nlink)` 输出点加 debug 计数日志（可编译开关）。
- 建立最小不变量检查：
  - `RefCount >= 0`
  - `Dentry.Inode != null` 时该 inode 必须包含该 dentry
- 交付物：
  - 新增 `InodeRefTrace` 诊断结构（仅 debug）
  - 新增 1 组“不变量单测”。
- 退出条件：
  - 当前主分支回归全绿，且日志能定位每次引用变化来源。

### Phase A1: Inode 基类双计数 API 落地

- 在 `Inode` 引入 `LinkCount`，并提供统一方法：
  - `AcquireRef(kind)` / `ReleaseRef(kind)`
  - `IncLink()` / `DecLink()`
  - `TryEvictCache()` / `OnEvictCache()`
  - `TryFinalizeDelete()` / `OnFinalizeDelete()`
- `Get/Put` 兼容壳已移除，调用方必须直接使用 `AcquireRef/ReleaseRef`。
- 交付物：
  - `Inode` 新 API
  - `stat/statx` 统一读取 `LinkCount`
  - `RefCount/LinkCount` 负值保护与断言。
- 退出条件：
  - 基础单测可覆盖 `create/link/unlink/open/close` 四条计数路径。

### Phase A2: Dentry 绑定原子化

- 引入：
  - `Dentry.BindInode(inode)` / `Dentry.UnbindInode()`
  - `Inode.AttachAliasDentry(dentry)` / `Inode.DetachAliasDentry(dentry)`
- 禁止外部直接操作 `inode.Dentries.Add/Remove`。
- 引入真实 `DentryRefCount(d_count)` 语义与 LRU 入队/出队规则。
- 交付物：
  - 全局替换手工 `Dentries.Add/Remove`
  - `PathWalker`、`VfsCacheReclaimer`、`rename/unlink` 路径对齐。
  - dentry 正/负节点回收规则可观测（debug trace）。
- 退出条件：
  - dentry 绑定不变量在测试与 debug 模式下持续成立。

### Phase B1: syscall/VFS 通路统一

- `LinuxFile` 构造与 `Close` 改用统一 ref API：
  - `ReferenceKind.Normal -> FileOpen`
  - `ReferenceKind.MmapHold -> FileMmap`
- `cwd/root/procRoot` pin 改为显式 `PathPin` 引用。
- `PathPin` 扩展为 `mount + dentry + inode(PathPin)` 三元对称引用。
- `clone(CLONE_FS)` 落地为共享 `fs_struct`；`clone(!CLONE_FS)` 复制 `fs_struct`。
- `HasActiveInodes` 改为新判据，不再依赖 `Dentries.Count`。
- 交付物：
  - `LinuxFile` / `VMAManager` / `SyscallManager` 引用路径统一
  - `umount` busy 判定语义更新。
- 退出条件：
  - `mmap + close(fd)`、`unlink + open fd`、`unlink + mmap` 回归稳定。
  - `CLONE_FS` 下 `chdir/chroot` 对共享方可见；非 `CLONE_FS` 下隔离可见。

### Phase B2: namespace 语义统一入口

- 将 `link/unlink/rmdir/rename-overwrite` 的 link 计数变化收敛到统一 helper：
  - `NamespaceOps.ApplyLinkDelta(...)`
- 所有 FS 不再在散点位置直接“猜测” link 变化。
- 交付物：
  - 公共 helper + 各 FS 接入
  - `rename exchange/overwrite` 计数回归。
- 退出条件：
  - `stat/statx` nlink 与预期一致，且跨 FS 一致。

### Phase B3: Shrinker 与缓存回收路径统一

- 引入统一 shrinker：
  - dcache shrink（`DentryRefCount==0`）
  - inode cache shrink（`RefCount==0`）
- 将 `drop_caches` 路径收敛为“cache reclaim only”，禁止业务删除副作用。
- 交付物：
  - 统一 `VfsShrinker` 接口与回收统计
  - `drop_caches` 覆盖 mode=1/2/3 语义回归
- 退出条件：
  - `RefCount==0 && LinkCount>0` 的 inode 可被回收并可 `iget` 重建
  - shrinker 不改变任何可见 `nlink` 语义

### Phase C: FS 逐个迁移（严格串行）

- 顺序：`IndexedMemoryFs/Tmpfs -> SilkFS -> HostFS -> OverlayFS -> LayerFS/ProcFS`
- 规则：
  - 每迁移一个 FS，先补测试再改实现；
  - 通过后再迁移下一个 FS。
- SilkFS 子阶段固定顺序：
  - `schema/事务改造 -> nlink语义对齐 -> orphan恢复 -> iget/evict缓存化`

### Phase D: 清理遗留逻辑

- 删除 FS 私有生命周期计数器（如 `_openCount/_openRefCount/_mmapRefCount` 的生死判据角色）。
- 删除基于 `Dentries.Count` 的近似判据。
- 收敛回收路径到：
  - cache evict：`TryEvictCache -> OnEvictCache`
  - final delete：`TryFinalizeDelete -> OnFinalizeDelete`
- 交付物：
  - 代码搜索中不再出现“私有计数决定 inode 生死”模式。

## 9.1 PR 切分建议（可直接执行）

1. PR-1: `Inode` 双计数字段 + cache/finalize 双阶段 API + `stat/statx` nlink 来源修正。  
2. PR-2: `Dentry` 绑定原子化 API + `DentryRefCount(d_count)` 引入。  
3. PR-3: `LinuxFile/mmap/path-pin` 引用来源统一。  
4. PR-4: `NamespaceOps` link 变化统一入口。  
5. PR-5: `VfsShrinker` + `drop_caches` mode=1/2/3 语义统一。  
6. PR-6: `IndexedMemoryFs/Tmpfs` 迁移。  
7. PR-7~9: `SilkFS`（事务、nlink、恢复/iget、evict）。  
8. PR-10~11: `HostFS`（inode identity + mount boundary）。  
9. PR-12: `OverlayFS/LayerFS/ProcFS` 收尾。  
10. PR-13: 删除兼容层与遗留计数逻辑。  

## 9.2 关键行为伪代码（统一语义）

### Inode 引用与回收

```text
AcquireRef(kind):
  RefCount += 1
  RefByKind[kind] += 1   // 可选，仅debug

ReleaseRef(kind):
  assert RefCount > 0
  RefCount -= 1
  RefByKind[kind] -= 1   // 可选，仅debug
  TryFinalizeDelete()

IncLink():
  LinkCount += 1

DecLink():
  assert LinkCount > 0
  LinkCount -= 1
  TryFinalizeDelete()

TryEvictCache():
  if IsCacheEvicted: return
  if RefCount != 0: return
  IsCacheEvicted = true
  OnEvictCache()

TryFinalizeDelete():
  if IsFinalized: return
  if RefCount != 0: return
  if LinkCount != 0: return
  IsFinalized = true
  OnFinalizeDelete()
  TryEvictCache()         // finalized inode 不再保留 cache 形态
```

### Dentry 绑定

```text
Dentry.Get():
  DentryRefCount += 1

Dentry.Put():
  assert DentryRefCount > 0
  DentryRefCount -= 1
  if DentryRefCount == 0:
    dcache_lru_add(this)

Dentry.BindInode(inode):
  assert this.Inode == null
  this.Inode = inode
  this.IsNegative = false
  inode.AttachAliasDentry(this)
  inode.AcquireRef(KernelInternal)

Dentry.UnbindInode():
  if this.Inode == null: return
  old = this.Inode
  this.Inode = null
  old.DetachAliasDentry(this)
  old.ReleaseRef(KernelInternal)
```

### open/mmap/close/munmap

```text
LinuxFile.ctor(kind):
  if kind == Normal: inode.AcquireRef(FileOpen)
  else if kind == MmapHold: inode.AcquireRef(FileMmap)
  inode.OnFileOpen(file)

LinuxFile.Close():
  inode.OnFileClose(file)
  if kind == Normal: inode.ReleaseRef(FileOpen)
  else if kind == MmapHold: inode.ReleaseRef(FileMmap)
```

### unlink / rename-overwrite

```text
Unlink(parent, name):
  victim = lookup(parent, name)
  namespace_remove(parent, name)   // dentry树解绑
  victim.DecLink()

Rename(oldParent, oldName, newParent, newName):
  src = lookup(oldParent, oldName)
  dst = lookup(newParent, newName) // may null
  namespace_move(oldParent, oldName, newParent, newName)
  if dst != null and dst != src:
    dst.DecLink()
  // src link count unchanged
```

### drop_caches（VFS层）

```text
DropCaches(mode):
  if mode has PAGECACHE:
    drop_pagecache()
  if mode has DCACHE_INODE:
    shrink_dcache()          // only DentryRefCount==0
    shrink_inode_cache()     // only RefCount==0
  // 注意: cache shrink 不改变 LinkCount
```

## 9.3 SilkFS 事务伪代码（SQLite 真相）

### link / unlink（事务内更新 nlink）

```text
TX link(parent_ino, name, target_ino):
  INSERT/UPSERT dentries(parent_ino, name, target_ino)
  UPDATE inodes SET nlink = nlink + 1 WHERE ino = target_ino
COMMIT

TX unlink(parent_ino, name):
  victim_ino = SELECT ino FROM dentries WHERE (parent_ino, name)
  DELETE FROM dentries WHERE (parent_ino, name)
  UPDATE inodes SET nlink = nlink - 1 WHERE ino = victim_ino
COMMIT
```

### evict / orphan 最终删除

```text
OnEvictCache(ino):
  // 前提: RefCount=0
  release_pagecache_and_incore_state()
  keep_sqlite_inode_row_if_nlink_gt_0()

OnFinalizeDelete(ino):
  // 前提: RefCount=0 && LinkCount=0
  TX:
    oldObj = SELECT object_id FROM inode_objects WHERE ino=@ino
    DELETE FROM inodes WHERE ino=@ino        // 级联清理 xattrs/inode_objects
    dec objects.refcount(oldObj)
  COMMIT
  delete live/{ino}.bin
  if oldObj refcount == 0: delete objects blob
```

### 挂载恢复（崩溃后）

```text
MountRecover():
  rebuild_object_refcount_from_inode_objects()
  for ino in SELECT ino FROM inodes WHERE nlink = 0 AND ino != ROOT:
    evict_inode_persistently(ino)   // 无运行时引用，直接清理
  load_root_then_lazy_iget()
```

## 10. 风险与约束

- HostFS 若继续 path-keyed inode，会持续偏离 Linux 语义；必须先完成 inode identity 改造。
- OverlayFS 视图 inode 与底层 inode 关系复杂，建议先保证“计数不出错”，再做性能优化。
- 兼容层已移除；禁止引入任何旧判据回归。

## 11. 验收标准（Definition of Done）

### 11.1 通用对象结构验收（Inode/Dentry/SuperBlock/LinuxFile）

1. Inode 双计数 + Dentry 引用计数为生命周期真相  
   - 每个 `Inode` 必须有 `RefCount` 与 `LinkCount`。  
   - 每个 `Dentry` 必须有 `DentryRefCount`（或等价 lockref 计数字段）。  
   - 生命周期判据仅允许使用 `RefCount/LinkCount`，禁止再用 `Dentries.Count` 近似推导。  
   - `RefCount < 0`、`LinkCount < 0` 或 `DentryRefCount < 0` 在 debug/test 中必须直接失败（断言或异常）。

2. 引用来源可追踪  
   - `AcquireRef/ReleaseRef` 必须带 `InodeRefKind`。  
   - `open/close`、`mmap/munmap`、`PathPin` 三类引用路径必须可在日志中区分。  
   - `Get/Put` 不得出现在代码库（仅保留 `AcquireRef/ReleaseRef` 路径）。

3. Dentry 绑定原子化  
   - `Dentry` 与 `Inode` 的绑定/解绑必须走统一 API（`BindInode/UnbindInode`）。  
   - 代码中不再允许散落 `inode.Dentries.Add/Remove`。  
   - 任意时刻保持不变量：`dentry.Inode == inode` 等价于 `inode.Dentries.Contains(dentry)`。
   - `BindInode/UnbindInode` 必须对称维护 inode 内部引用（`KernelInternal`）。

4. SuperBlock 跟踪一致  
   - 每个 FS 新建 inode 必须注册到统一 inode 跟踪集合。  
   - `umount` busy 判定只能基于统一生命周期模型（而非 FS 私有状态）。

5. nlink 对外可见语义统一  
   - `stat/statx` 的 `st_nlink`/`nlink` 必须统一读取 `Inode.LinkCount`。  
   - 不允许出现某路径固定写 1、另一路径按缓存推导的分裂行为。

6. 回收路径唯一  
   - cache 回收入口仅允许 `TryEvictCache -> OnEvictCache`。  
   - 最终删除入口仅允许 `TryFinalizeDelete -> OnFinalizeDelete`。  
   - `drop_caches` 只允许触发 cache reclaim，不得改变 link 语义与 namespace 可见性。

### 11.2 通用 syscall 语义验收

1. `open/close` 只影响 `RefCount`。  
2. `mmap/munmap` 只影响 `RefCount`，`close(fd)` 不得提前回收 mapping 关联 inode。  
3. `link/unlink` 只影响 `LinkCount`。  
4. `rename` 非覆盖不改 source link；覆盖目标仅对 victim `LinkCount--`。  
5. `unlink + open`、`unlink + mmap` 场景必须满足 Linux orphan 语义：`LinkCount=0` 但数据仍可访问直到最后引用释放。
6. `drop_caches` mode=2 下允许回收 `RefCount==0 && LinkCount>0` 的 inode cache，并可在后续路径访问中 `iget` 重建。

### 11.3 各文件系统实现约束验收

1. IndexedMemoryFs/Tmpfs  
   - `create/mkdir/mknod/symlink` 后 `LinkCount` 初值正确。  
   - `link/unlink/rmdir/rename-overwrite` 的 link 变化严格符合统一规则。  
   - 不再由 `_openCount` 类私有计数决定 inode 生死。

2. SilkFS  
   - SQLite `inodes.nlink` 与内存 `LinkCount` 语义一致，且通过事务原子更新。  
   - `dentries` 与 `nlink` 在 `create/link/unlink/rename` 后保持一致。  
   - `nlink=0 && ref>0` orphan 在最后引用释放前可继续 I/O。  
   - 挂载恢复会清理 `nlink=0` 残留 inode。  
   - inode 被内存回收后可按 `ino` 无损重建（`iget` 语义）。

3. HostFS  
   - inode 身份必须基于对象身份（Linux/macOS: `dev+ino`; Windows: volume+fileId），不能仅靠路径。  
   - hardlink 别名路径下，`LinkCount` 与实际命名空间一致。  
   - mount 边界行为符合策略定义（默认单挂载域隔离）。  
   - 特殊对象（symlink/reparse/socket/device/fifo）按策略返回一致行为或错误码，不得静默降级。

4. OverlayFS  
   - overlay namespace 操作不破坏 upper/lower 生命周期边界。  
   - copy-up、whiteout、rename-overwrite 后 link 语义与上层可见命名空间一致。  
   - 不允许通过白化逻辑绕过 `LinkCount` 变更约束。

5. LayerFS（只读）  
   - 只读语义下 `LinkCount` 初始化正确，运行期不出现非法 link 变更。  
   - 可被 cache 回收但不得出现过早回收导致的可达对象失效。

6. ProcFS/DevPts/Anon inode  
   - 路径可见节点与匿名节点的 link 策略一致且文档化。  
   - 不因动态生成/回收造成负计数或悬挂 dentry。

### 11.4 测试与质量门槛（必须全部满足）

1. 回归覆盖  
   - 必须包含：`mmap + close(fd)`、`unlink + open`、`unlink + mmap`、`rename overwrite`、`drop_caches`、`umount busy`。  
   - 必须包含：`drop_caches(mode=2)` 后 `iget` 重建、以及“持有打开 fd 时 drop_caches 不破坏 I/O”。  
   - 必须包含：`clone(CLONE_FS)` 共享 cwd/root 可见性、`clone(!CLONE_FS)` 隔离可见性。  
   - 各 FS 专项回归必须覆盖其对应约束（见 11.3）。

2. 代码约束扫描  
   - 代码库中不再存在“FS 私有计数器决定 inode 生死”的分支。  
   - 代码库中不再存在业务路径对 `inode.Dentries` 的直接 `Add/Remove`。
   - 代码库中必须存在统一 shrinker 入口，且不允许在 shrink 路径直接改 `LinkCount`。

3. 观测性  
   - 在 debug 构建下，可导出单个 inode 的 ref/link 变化时间序列（至少含 kind、调用点、前后值）。  
   - 任一验收失败场景能通过日志定位到具体计数变化路径。
