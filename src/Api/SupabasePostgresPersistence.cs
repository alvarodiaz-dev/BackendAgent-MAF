using System;
using System.Threading.Tasks;
using Npgsql;

namespace BasicAgent.Api
{
    internal sealed class SupabasePostgresPersistence : IPipelinePersistence
    {
        private readonly string _connectionString;

        public SupabasePostgresPersistence(string connectionString)
        {
            _connectionString = connectionString;
        }

        public bool IsEnabled => true;

        public Task EnsureSchemaAsync()
        {
            const string sql = """
                create extension if not exists pgcrypto;

                create table if not exists chat_sessions (
                    id uuid primary key,
                    title text null,
                    status text not null default 'active',
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
                );

                create table if not exists chat_messages (
                    id uuid primary key default gen_random_uuid(),
                    session_id uuid not null references chat_sessions(id) on delete cascade,
                    run_id text null,
                    role text not null,
                    content text not null,
                    metadata jsonb not null default '{}'::jsonb,
                    created_at timestamptz not null default now()
                );

                create table if not exists pipeline_runs (
                    id text primary key,
                    session_id uuid not null references chat_sessions(id) on delete cascade,
                    prompt text not null,
                    status text not null,
                    current_phase text null,
                    error text null,
                    run_directory text not null,
                    auto_approve boolean not null default false,
                    started_at timestamptz not null default now(),
                    finished_at timestamptz null
                );

                create table if not exists pipeline_events (
                    id bigserial primary key,
                    run_id text not null references pipeline_runs(id) on delete cascade,
                    event_type text not null,
                    payload jsonb not null default '{}'::jsonb,
                    created_at timestamptz not null default now()
                );

                create table if not exists pipeline_checkpoints (
                    id uuid primary key default gen_random_uuid(),
                    run_id text not null references pipeline_runs(id) on delete cascade,
                    phase text not null,
                    step_name text not null,
                    state jsonb not null default '{}'::jsonb,
                    ordinal int not null default 1,
                    created_at timestamptz not null default now()
                );

                create table if not exists run_confirmations (
                    id uuid primary key default gen_random_uuid(),
                    run_id text not null references pipeline_runs(id) on delete cascade,
                    request_id text not null unique,
                    prompt text not null,
                    status text not null,
                    response text null,
                    created_at timestamptz not null default now(),
                    answered_at timestamptz null
                );

                create index if not exists ix_chat_messages_session_created on chat_messages(session_id, created_at);
                create index if not exists ix_pipeline_runs_session_started on pipeline_runs(session_id, started_at desc);
                create index if not exists ix_pipeline_events_run_created on pipeline_events(run_id, created_at);
                create index if not exists ix_checkpoints_run_created on pipeline_checkpoints(run_id, created_at desc);
                create index if not exists ix_run_confirmations_run_status on run_confirmations(run_id, status);
                """;

            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }

        public Task UpsertSessionAsync(Guid sessionId, string? title)
        {
            const string sql = """
                insert into chat_sessions(id, title, status)
                values(@id, @title, 'active')
                on conflict(id) do update
                set title = coalesce(excluded.title, chat_sessions.title),
                    updated_at = now();
                """;

            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", sessionId);
            cmd.Parameters.AddWithValue("title", (object?)title ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }

        public Task InsertMessageAsync(Guid sessionId, string role, string content, string? runId = null)
        {
            const string sql = """
                insert into chat_messages(session_id, run_id, role, content)
                values(@session_id, @run_id, @role, @content);
                """;

            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("session_id", sessionId);
            cmd.Parameters.AddWithValue("run_id", (object?)runId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("role", role);
            cmd.Parameters.AddWithValue("content", content);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }

        public Task CreateRunAsync(string runId, Guid sessionId, string prompt, string status, string currentPhase, string runDirectory, bool autoApprove)
        {
            const string sql = """
                insert into pipeline_runs(id, session_id, prompt, status, current_phase, run_directory, auto_approve)
                values(@id, @session_id, @prompt, @status, @current_phase, @run_directory, @auto_approve)
                on conflict(id) do update
                set status = excluded.status,
                    current_phase = excluded.current_phase,
                    prompt = excluded.prompt,
                    run_directory = excluded.run_directory,
                    auto_approve = excluded.auto_approve;
                """;

            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", runId);
            cmd.Parameters.AddWithValue("session_id", sessionId);
            cmd.Parameters.AddWithValue("prompt", prompt);
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.AddWithValue("current_phase", currentPhase);
            cmd.Parameters.AddWithValue("run_directory", runDirectory);
            cmd.Parameters.AddWithValue("auto_approve", autoApprove);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }

        public Task UpdateRunAsync(string runId, string status, string? currentPhase, string? error)
        {
            const string sql = """
                update pipeline_runs
                set status = @status,
                    current_phase = @current_phase,
                    error = @error,
                    finished_at = case when @status in ('completed','failed','canceled') then now() else finished_at end
                where id = @id;
                """;

            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", runId);
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.AddWithValue("current_phase", (object?)currentPhase ?? DBNull.Value);
            cmd.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }

        public Task AppendEventAsync(string runId, string eventType, string payloadJson)
        {
            const string sql = """
                insert into pipeline_events(run_id, event_type, payload)
                values(@run_id, @event_type, @payload::jsonb);
                """;

            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("run_id", runId);
            cmd.Parameters.AddWithValue("event_type", eventType);
            cmd.Parameters.AddWithValue("payload", payloadJson);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }

        public Task AddCheckpointAsync(string runId, string phase, string stepName, string stateJson)
        {
            const string sql = """
                insert into pipeline_checkpoints(run_id, phase, step_name, state)
                values(@run_id, @phase, @step_name, @state::jsonb);
                """;

            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("run_id", runId);
            cmd.Parameters.AddWithValue("phase", phase);
            cmd.Parameters.AddWithValue("step_name", stepName);
            cmd.Parameters.AddWithValue("state", stateJson);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }

        public Task UpsertConfirmationAsync(string runId, string requestId, string prompt, string status, string? response)
        {
            const string sql = """
                insert into run_confirmations(run_id, request_id, prompt, status, response)
                values(@run_id, @request_id, @prompt, @status, @response)
                on conflict(request_id) do update
                set status = excluded.status,
                    response = excluded.response,
                    answered_at = case when excluded.status = 'answered' then now() else run_confirmations.answered_at end;
                """;

            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("run_id", runId);
            cmd.Parameters.AddWithValue("request_id", requestId);
            cmd.Parameters.AddWithValue("prompt", prompt);
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.AddWithValue("response", (object?)response ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }
    }
}