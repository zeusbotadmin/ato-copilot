import Mocha from "mocha";
import * as path from "path";
import * as fs from "fs";

function run(): void {
  const mocha = new Mocha({
    ui: "bdd",
    color: true,
    timeout: 10000,
  });

  // Tests live under multiple folders so groups stay logically separate.
  // `runTests` walks each folder one level deep and registers `*.test.js`.
  // (Feature 051 added `auth/` for the device-code sign-in suites.)
  const testFolders = ["suite", "auth"];

  for (const folder of testFolders) {
    const testsRoot = path.resolve(__dirname, folder);
    if (!fs.existsSync(testsRoot)) continue;
    const files = fs
      .readdirSync(testsRoot)
      .filter((f) => f.endsWith(".test.js"));
    for (const file of files) {
      mocha.addFile(path.resolve(testsRoot, file));
    }
  }

  mocha.run((failures) => {
    if (failures > 0) {
      console.error(`${failures} test(s) failed.`);
      process.exit(1);
    }
  });
}

run();
