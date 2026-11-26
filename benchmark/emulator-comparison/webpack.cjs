const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const webpack = require("webpack");
const TerserPlugin = require("terser-webpack-plugin");

const output = path.resolve(__dirname, "../out");
const hashOptions = process.argv.includes("--native-md4") ? { hashFunction: "native-md4" } : {};
webpack({
    mode: "production",
    context: __dirname,
    entry: "./src/entry.js",
    output: { path: output, filename: "bundle.js", ...hashOptions },
    cache: false,
    optimization: { minimizer: [new TerserPlugin({ parallel: false })] }
}, (error, stats) => {
    if (error || stats.hasErrors()) {
        console.error(error || stats.toString({ all: false, errors: true }));
        process.exitCode = 1;
        return;
    }
    const bundle = fs.readFileSync(path.join(output, "bundle.js"));
    console.log("BUNDLE_SHA256 " + crypto.createHash("sha256").update(bundle).digest("hex"));
});
