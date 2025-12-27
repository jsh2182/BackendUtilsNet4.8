# ⚙️ BackendUtilsNet4.8

**BackendUtilsNet4.8** is a **utility library for .NET Framework 4.8** providing a set of backend tools and extensions for enterprise applications. It simplifies common backend operations and integrates with multiple services such as Asterisk, push notifications, and database extensions.

---

## 🧩 Project Overview

The library offers the following core functionalities:

* **Asterisk Integration:** Easily connect and interact with Asterisk PBX systems.
* **Push Notifications:** Support for Web Push notifications to web clients.
* **Server-Sent Events (SSE):** Real-time notifications via SSE.
* **Entity Framework Extensions:** Bulk operations and performance optimizations for database operations.

This project is designed to provide reusable backend utilities, reduce boilerplate code, and improve productivity for .NET 4.8 projects.

---

## 🚀 Key Features

* **Asterisk Communication:** Send and receive events and commands from Asterisk PBX.
* **Web Push Notifications:** Send notifications to web browsers using standard Web Push protocols.
* **Server-Sent Events (SSE):** Stream real-time events to clients efficiently.
* **Entity Framework Extensions:** Bulk insert, update, and delete operations for EF to handle large datasets.
* **Modular Utility Classes:** Easily integrate individual utilities without adding unnecessary dependencies.

---

## 🛠 Architecture

```
BackendUtilsNet4.8.sln
├── AsteriskService/       # Asterisk communication module
├── Notifications/         # Push Notification & SSE handling module
├── Extensions/            # EF bulk operations and other extensions
├── lib/                   # Third-party libraries and dependencies
├── Properties/            # Project properties
├── BackendUtilsNet4.8.csproj
└── README.md
```

* **AsteriskService Module:** Handles connections, events, and command execution for Asterisk PBX.
* **Notifications Module:** Provides Web Push and SSE classes for real-time client notifications.
* **Extensions Module:** Contains EF bulk operations and helper extension methods for backend efficiency.
* **lib Folder:** Holds third-party dependencies used across modules.

---

## 📦 Tech Stack

| Layer           | Technology          |
| --------------- | ------------------- |
| Framework       | .NET Framework 4.8  |
| ORM             | Entity Framework 6+ |
| Notification    | Web Push, SSE       |
| PBX Integration | Asterisk            |
| Build Tools     | Visual Studio       |

---

## 📌 Getting Started

### Prerequisites

* Visual Studio 2017+
* .NET Framework 4.8
* SQL Server or compatible database (for EF extensions)
* Asterisk server (if using Asterisk module)

### Setup

1. Clone the repository:

   ```bash
   git clone https://github.com/jsh2182/BackendUtilsNet4.8.git
   cd BackendUtilsNet4.8
   ```

2. Restore NuGet packages and build the solution in Visual Studio.

3. Reference required modules in your project.

4. Configure modules as needed (e.g., Asterisk connection strings, push subscription info).

---

## 🧪 Usage Examples

### Asterisk Integration

```csharp
var asteriskClient = new AsteriskClient("host", 5038, "user", "password");
asteriskClient.Connect();
asteriskClient.SendCommand("Originate ...");
```

### Push Notifications

```csharp
var pushService = new PushNotificationService();
pushService.SendNotification(userId, "New message received");
```

### EF Bulk Insert

```csharp
using (var context = new MyDbContext())
{
    context.BulkInsert(entitiesList);
}
```

### Server-Sent Events (SSE)

```csharp
var sse = new SseHandler();
sse.SendEvent("update", "Data updated");
```

---

## 🎯 Use Cases

* Enterprise backend projects needing reusable utility components.
* Applications requiring integration with Asterisk PBX.
* Systems needing real-time notifications (SSE, Web Push).
* Projects with heavy database operations benefiting from EF bulk extensions.

---

## 🚀 Future Improvements

* Add additional modules for logging, caching, and authentication helpers.
* Support for async/await in all modules.
* Improved documentation and code samples.
* Integration tests for all modules.

---

## 📝 License

MIT License (or specify your license here)

> *(Include LICENSE file in the repo)*
