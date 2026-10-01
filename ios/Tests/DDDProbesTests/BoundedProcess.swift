#if os(macOS)
import Darwin
import Foundation

/// File-backed stdio avoids pipe-buffer deadlocks, including a child that never reads stdin.
enum BoundedProcess {
    struct Result {
        let status: Int32
        let stdout: Data
        let stderr: Data
        var text: String { String(decoding: stdout, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines) }
    }

    enum Failure: Error {
        case timeout(executable: String, arguments: [String])
        case unsuccessful(executable: String, status: Int32, stderr: String)
    }

    static func run(_ executable: String, _ arguments: [String], in directory: URL,
                    input: Data = Data(), environment: [String: String]? = nil,
                    timeout: TimeInterval = 10) throws -> Result {
        let scratch = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: scratch, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: scratch) }
        let inputURL = scratch.appendingPathComponent("stdin")
        let outputURL = scratch.appendingPathComponent("stdout")
        let errorURL = scratch.appendingPathComponent("stderr")
        try input.write(to: inputURL)
        try Data().write(to: outputURL)
        try Data().write(to: errorURL)
        let stdin = try FileHandle(forReadingFrom: inputURL)
        let stdout = try FileHandle(forWritingTo: outputURL)
        let stderr = try FileHandle(forWritingTo: errorURL)
        defer { try? stdin.close(); try? stdout.close(); try? stderr.close() }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        process.currentDirectoryURL = directory
        process.environment = environment
        process.standardInput = stdin
        process.standardOutput = stdout
        process.standardError = stderr
        let finished = DispatchSemaphore(value: 0)
        process.terminationHandler = { _ in finished.signal() }
        try process.run()
        if finished.wait(timeout: .now() + timeout) == .timedOut {
            if process.isRunning { process.terminate() }
            if finished.wait(timeout: .now() + 0.5) == .timedOut {
                if process.isRunning { _ = kill(process.processIdentifier, SIGKILL) }
                _ = finished.wait(timeout: .now() + 0.5)
            }
            // No unbounded waitUntilExit, including after escalation.
            throw Failure.timeout(executable: executable, arguments: arguments)
        }
        return Result(status: process.terminationStatus,
                      stdout: try Data(contentsOf: outputURL), stderr: try Data(contentsOf: errorURL))
    }

    static func checked(_ executable: String, _ arguments: [String], in directory: URL,
                        input: Data = Data(), environment: [String: String]? = nil) throws -> Result {
        let result = try run(executable, arguments, in: directory, input: input, environment: environment)
        guard result.status == 0 else {
            throw Failure.unsuccessful(executable: executable, status: result.status,
                                       stderr: String(decoding: result.stderr, as: UTF8.self))
        }
        return result
    }
}
#endif
