package com.brighton.indexer;

import com.sun.net.httpserver.HttpServer;
import java.io.*;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.regex.*;

public class IndexerApplication {
    private static final String UPLOAD_DIR = System.getenv().getOrDefault("UPLOAD_DIR", "/shared/uploads");
    private static final String KB_FILE = System.getenv().getOrDefault("KB_FILE", "/shared/kb/knowledge_base.json");

    public static void main(String[] args) throws Exception {
        HttpServer server = HttpServer.create(new InetSocketAddress(8080), 0);

        server.createContext("/health", ex -> {
            byte[] resp = ("{\"status\":\"healthy\",\"service\":\"doc-indexer-spring\",\"uploadDir\":\"" + UPLOAD_DIR + "\"}").getBytes(StandardCharsets.UTF_8);
            ex.getResponseHeaders().add("Content-Type", "application/json");
            ex.sendResponseHeaders(200, resp.length);
            try (OutputStream os = ex.getResponseBody()) { os.write(resp); }
        });

        server.createContext("/api/index/sync", ex -> {
            if (!"POST".equalsIgnoreCase(ex.getRequestMethod())) {
                ex.sendResponseHeaders(405, -1);
                return;
            }
            byte[] resp = syncPdfs().getBytes(StandardCharsets.UTF_8);
            ex.getResponseHeaders().add("Content-Type", "application/json");
            ex.sendResponseHeaders(200, resp.length);
            try (OutputStream os = ex.getResponseBody()) { os.write(resp); }
        });

        server.start();
        System.out.println("Java PDF Indexer listening on port 8080");
    }

    private static synchronized String syncPdfs() throws Exception {
        Path kbPath = Paths.get(KB_FILE);
        Files.createDirectories(kbPath.getParent());
        List<String[]> entries = new ArrayList<>();

        if (Files.exists(kbPath)) {
            String raw = Files.readString(kbPath, StandardCharsets.UTF_8);
            Matcher m = Pattern.compile("\"topic\"\s*:\s*\"(.*?)\"\s*,\s*\"content\"\s*:\s*\"(.*?)\"", Pattern.DOTALL).matcher(raw);
            while (m.find()) {
                entries.add(new String[]{unescape(m.group(1)), unescape(m.group(2))});
            }
        }

        File folder = new File(UPLOAD_DIR);
        File[] pdfs = folder.listFiles((dir, name) -> name.toLowerCase().endsWith(".pdf"));
        int indexedNew = 0;

        if (pdfs != null) {
            for (File pdf : pdfs) {
                String topic = "Uploaded PDF: " + pdf.getName();
                boolean exists = entries.stream().anyMatch(e -> e[0].equals(topic));
                if (!exists) {
                    String text = extractPdfText(pdf);
                    if (text.isEmpty()) text = "Uploaded PDF document: " + pdf.getName() + " (" + pdf.length() + " bytes)";
                    if (text.length() > 1200) text = text.substring(0, 1200);
                    entries.add(new String[]{topic, text});
                    indexedNew++;
                }
            }
        }

        StringBuilder sb = new StringBuilder("[\n");
        for (int i = 0; i < entries.size(); i++) {
            sb.append("  {\"topic\": \"").append(escape(entries.get(i)[0]))
              .append("\", \"content\": \"").append(escape(entries.get(i)[1])).append("\"}");
            if (i < entries.size() - 1) sb.append(",");
            sb.append("\n");
        }
        sb.append("]\n");
        Files.writeString(kbPath, sb.toString(), StandardCharsets.UTF_8);

        return "{\"indexedNewFiles\":" + indexedNew + ",\"totalKnowledgeEntries\":" + entries.size() + "}";
    }

    private static String extractPdfText(File pdf) {
        try {
            String raw = new String(Files.readAllBytes(pdf.toPath()), StandardCharsets.ISO_8859_1);
            StringBuilder out = new StringBuilder();
            Matcher m = Pattern.compile("\\(([^()\\\\]{2,})\\)\\s*Tj").matcher(raw);
            while (m.find()) {
                out.append(m.group(1)).append(" ");
            }
            return out.toString().replaceAll("\\s+", " ").trim();
        } catch (Exception e) {
            return "";
        }
    }

    private static String escape(String s) {
        return s.replace("\\", "\\\\").replace("\"", "\\\"").replace("\n", " ").replace("\r", " ");
    }

    private static String unescape(String s) {
        return s.replace("\\\"", "\"").replace("\\\\", "\\");
    }
}
