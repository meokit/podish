import {readFile} from 'node:fs/promises';
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';

const usage = 'Usage: node Podish.Browser/node.mjs <rootfs.tar> </guest/executable> [guest arguments...]';
const [rootfs, executable, ...args] = process.argv.slice(2);
if (rootfs === '--help') {
    console.log(usage);
} else if (!rootfs || !executable?.startsWith('/')) {
    console.error(usage);
    process.exitCode = 2;
} else {
    try {
        const appDir = process.env.PODISH_WASM_APP_DIR;
        const loader = appDir
            ? pathToFileURL(resolve(appDir, '_framework/dotnet.js'))
            : new URL('bin/Release/net11.0/publish/wwwroot/_framework/dotnet.js', import.meta.url);
        const tar = await readFile(rootfs);
        const {dotnet} = await import(loader.href);
        const runtime = await dotnet.create();
        const exports = await runtime.getAssemblyExports('Podish.Browser');
        const exitCode = await exports.Podish.Browser.NodeExports.RunRootfs(tar, executable, args);
        // Flush Emscripten's line-buffered stdio before Node exits.
        runtime.Module.FS.quit();
        process.exitCode = exitCode;
    } catch (error) {
        console.error(error);
        process.exitCode = 1;
    }
}
