package com.brighton.indexer;
import com.sun.net.httpserver.*;
import java.io.*;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.regex.*;

public class IndexerApplication {
    public static void main(String[] args) throws Exception {
        String upDir = System.getenv().getOrDefault("UPLOAD_DIR", "/shared/uploads");
        String propFile = System.getenv().getOrDefault("PROPOSALS_FILE", "/shared/kb/proposals_offers.json");
        HttpServer srv = HttpServer.create(new InetSocketAddress(8080), 0);
        srv.createContext("/health", ex -> send(ex, "{\"status\":\"healthy\"}"));
        srv.createContext("/api/index/sync", ex -> {
            try {
                send(ex, sync(upDir, propFile));
            } catch (Exception e) {
                send(ex, "{\"error\":\"" + e.getMessage().replace("\"", "'") + "\"}");
            }
        });
        srv.start();
        System.out.println("Offers & Proposals Indexer ready on 8080");
    }
    static void send(HttpExchange ex, String body) throws IOException {
        byte[] b = body.getBytes(StandardCharsets.UTF_8);
        ex.getResponseHeaders().add("Content-Type", "application/json");
        ex.sendResponseHeaders(200, b.length);
        try (OutputStream os = ex.getResponseBody()) { os.write(b); }
    }
    static synchronized String sync(String upDir, String propFile) throws Exception {
        Path pf = Paths.get(propFile);
        Files.createDirectories(pf.getParent());
        String existing = Files.exists(pf) ? Files.readString(pf) : "[]";
        String inner = existing.trim();
        if (inner.startsWith("[")) inner = inner.substring(1, inner.length() - 1).trim();
        StringBuilder sb = new StringBuilder(inner);
        File[] pdfs = new File(upDir).listFiles((d, n) -> n.toLowerCase().endsWith(".pdf"));
        int added = 0;
        if (pdfs != null) {
            for (File f : pdfs) {
                String topic = "Offer/Proposal PDF: " + f.getName();
                if (!existing.contains(topic)) {
                    String raw = new String(Files.readAllBytes(f.toPath()), StandardCharsets.ISO_8859_1);
                    Matcher m = Pattern.compile("\\(([^()\\\\]{2,})\\)\\s*Tj").matcher(raw);
                    StringBuilder txt = new StringBuilder();
                    while (m.find()) txt.append(m.group(1)).append(" ");
                    String clean = txt.toString().replaceAll("\\s+", " ").trim();
                    if (clean.isEmpty()) clean = "Offer/Proposal document: " + f.getName() + " (" + f.length() + " bytes)";
                    if (clean.length() > 1500) clean = clean.substring(0, 1500);
                    clean = clean.replace("\\", "\\\\").replace("\"", "\\\"");
                    if (sb.length() > 0) sb.append(",\n");
                    sb.append("  {\"topic\": \"").append(topic).append("\", \"content\": \"").append(clean).append("\"}");
                    added++;
                }
            }
        }
        String finalJson = "[\n" + sb + "\n]\n";
        Files.writeString(pf, finalJson, StandardCharsets.UTF_8);
        int total = finalJson.split("\"topic\"").length - 1;
        return "{\"indexedNewProposals\":" + added + ",\"totalProposals\":" + total + "}";
    }
}
