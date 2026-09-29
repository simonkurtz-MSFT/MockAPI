export default class PlaywrightNoSkipsReporter {
  skippedTests = [];

  onTestEnd(test, result) {
    if (result.status === "skipped") {
      this.skippedTests.push(`${test.location.file}:${test.location.line} ${test.titlePath().join(" > ")}`);
    }
  }

  onEnd() {
    if (this.skippedTests.length === 0) {
      return;
    }

    console.error(`Unexpectedly skipped browser tests:\n${this.skippedTests.join("\n")}`);
    return { status: "failed" };
  }
}
