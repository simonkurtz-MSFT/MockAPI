const childProcess = require("node:child_process");

const SAFE_WINDOWS_TOKEN = /^[A-Za-z0-9@/_.:=+~-]+$/;

function getExecutableInvocation(
  command,
  arguments_,
  { platform = process.platform, comSpec = process.env.ComSpec } = {}
) {
  if (platform !== "win32") {
    return { executable: command, arguments_ };
  }
  if (!comSpec) {
    throw new Error("ComSpec is unavailable; pnpm cannot be invoked on Windows.");
  }

  const tokens = [command, ...arguments_];
  const unsafeToken = tokens.find((token) => !SAFE_WINDOWS_TOKEN.test(token));
  if (unsafeToken) {
    throw new Error(`Refusing to pass an unsafe command token to cmd.exe: ${unsafeToken}`);
  }
  return {
    executable: comSpec,
    arguments_: ["/d", "/s", "/c", tokens.join(" ")],
  };
}

function runExecutable(command, arguments_, options) {
  const invocation = getExecutableInvocation(command, arguments_);
  return childProcess.execFileSync(invocation.executable, invocation.arguments_, options);
}

module.exports = {
  getExecutableInvocation,
  runExecutable,
};
