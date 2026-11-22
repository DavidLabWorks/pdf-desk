import PDFKit
import SwiftUI
import UniformTypeIdentifiers

@main
struct PDFDeskApp: App {
    var body: some Scene {
        DocumentGroup(viewing: PDFFile.self) { file in
            PDFReader(document: file.document.pdf)
                .frame(minWidth: 480, minHeight: 320)
        }
    }
}

struct PDFFile: FileDocument {
    static let readableContentTypes: [UTType] = [.pdf]
    static let writableContentTypes: [UTType] = []
    let pdf: PDFDocument

    init(configuration: ReadConfiguration) throws {
        guard let data = configuration.file.regularFileContents,
              let document = PDFDocument(data: data) else {
            throw CocoaError(.fileReadCorruptFile)
        }
        pdf = document
    }

    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        throw CocoaError(.featureUnsupported)
    }
}

private struct PDFReader: NSViewRepresentable {
    let document: PDFDocument

    func makeNSView(context: Context) -> PDFView {
        let view = PDFView()
        view.autoScales = true
        view.displayMode = .singlePageContinuous
        return view
    }

    func updateNSView(_ view: PDFView, context: Context) {
        if view.document !== document {
            view.document = document
        }
    }
}
