// mtlcheck <file.metal>... : compile each file with the OS's runtime Metal compiler (what the Unity player
// uses for its MSL), print failures, exit 1 if any failed. Text after "/* reflection" is ignored.
import Metal
import Foundation

let dev = MTLCreateSystemDefaultDevice()!
var failed = 0
let opts = MTLCompileOptions()
for path in CommandLine.arguments.dropFirst() {
    var src = try! String(contentsOfFile: path, encoding: .utf8)
    if let r = src.range(of: "/* reflection") { src = String(src[..<r.lowerBound]) }
    do { _ = try dev.makeLibrary(source: src, options: opts) }
    catch { failed += 1; print("FAIL \(path)\n\(error.localizedDescription.prefix(600))") }
}
print("\(CommandLine.arguments.count - 1 - failed) ok, \(failed) failed")
exit(failed == 0 ? 0 : 1)
