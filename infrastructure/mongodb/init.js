const applicationDatabase = db.getSiblingDB("kalendar");
const commonActions = ["find", "insert", "update", "remove", "createCollection", "createIndex", "listIndexes"];

applicationDatabase.createRole({
    role: "kalendarWebApi",
    privileges: [
        { resource: { db: "kalendar", collection: "users" }, actions: commonActions },
        { resource: { db: "kalendar", collection: "tasks" }, actions: commonActions },
        { resource: { db: "kalendar", collection: "taskLists" }, actions: commonActions },
        { resource: { db: "kalendar", collection: "" }, actions: ["listCollections"] }
    ],
    roles: []
});

applicationDatabase.createRole({
    role: "kalendarWorker",
    privileges: [
        { resource: { db: "kalendar", collection: "backgroundJobs" }, actions: commonActions },
        { resource: { db: "kalendar", collection: "" }, actions: ["listCollections"] }
    ],
    roles: []
});

applicationDatabase.createUser({
    user: _getEnv("KALENDAR_WEBAPI_MONGO_USER"),
    pwd: _getEnv("KALENDAR_WEBAPI_MONGO_PASSWORD"),
    roles: [{ role: "kalendarWebApi", db: "kalendar" }]
});

applicationDatabase.createUser({
    user: _getEnv("KALENDAR_WORKER_MONGO_USER"),
    pwd: _getEnv("KALENDAR_WORKER_MONGO_PASSWORD"),
    roles: [{ role: "kalendarWorker", db: "kalendar" }]
});
