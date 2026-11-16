package com.brighton.indexer;

import com.fasterxml.jackson.core.type.TypeReference;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.apache.pdfbox.Loader;
import org.apache.pdfbox.pdmodel.PDDocument;
import org.apache.pdfbox.text.PDFTextStripper;
import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.web.bind.annotation.*;

import java.io.File;
import java.nio.file.*;
import java.util.*;

@SpringBootApplication
@RestController
public class IndexerApplication {

    private final ObjectMapper mapper = new ObjectMapper();
    private final String uploadDir = System.getenv().getOrDefault("UPLOAD_DIR", "/shared/uploads");
    private final String kbFile = System.getenv().getOrDefault("KB_FILE", "/shared/kb/knowledge_base.json");

    public static void main(String[] args) {
        SpringApplication.run(IndexerApplication.class, args);
    }

    @GetMapping("/health")
    public Map<String, Object> health() {
        return Map.of("status", "healthy", "service", "doc-indexer-spring", "uploadDir", uploadDir);
    }

    @PostMapping("/api/index/sync")
    public Map<String, Object> syncPdfsToKnowledgeBase() throws Exception {
        Path kbPath = Paths.get(kbFile);
        Files.createDirectories(kbPath.getParent());
        List> kb = Files.exists(kbPath)
                ? mapper.readValue(kbPath.toFile(), new TypeReference<>() {})
                : new ArrayList<>();

        File folder = new File(uploadDir);
        File[] pdfs = folder.listFiles((dir, name) -> name.toLowerCase().endsWith(".pdf"));
        int indexedCount = 0;

        if (pdfs != null) {
            for (File pdf : pdfs) {
                String topicTag = "Uploaded PDF: " + pdf.getName();
                boolean alreadyIndexed = kb.stream().anyMatch(entry -> topicTag.equals(entry.get("topic")));
                if (!alreadyIndexed) {
                    try (PDDocument doc = Loader.loadPDF(pdf)) {
                        String text = new PDFTextStripper().getText(doc).replaceAll("\\s+", " ").trim();
                        String snippet = text.length() > 1200 ? text.substring(0, 1200) : text;
                        kb.add(Map.of("topic", topicTag, "content", snippet));
                        indexedCount++;
                    }
                }
            }
        }

        mapper.writerWithDefaultPrettyPrinter().writeValue(kbPath.toFile(), kb);
        return Map.of("indexedNewFiles", indexedCount, "totalKnowledgeEntries", kb.size());
    }
}
