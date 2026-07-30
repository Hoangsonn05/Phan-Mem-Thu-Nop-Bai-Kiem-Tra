-- File: backend/supabase/migrations/20260729002300_V23__add_sync_rpc_functions.sql
BEGIN;

-- 1. Cập nhật Schema Version trong bảng Meta
UPDATE public.examtransfer_cloud_meta 
SET schema_version = 23, updated_at = NOW() 
WHERE id = 1;

-- 2. RPC a: Upsert bài nộp (Last-Write-Wins dựa vào submitted_at)
CREATE OR REPLACE FUNCTION public.save_public_quiz_grade(
    p_student_id UUID,
    p_exam_room_id UUID,
    p_file_hash TEXT,
    p_submitted_at TIMESTAMPTZ,
    p_payload JSONB
)
RETURNS TABLE (submission_id UUID, status TEXT) 
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
DECLARE
    v_sub_id UUID;
BEGIN
    INSERT INTO public.submissions (
        student_id,
        exam_room_id,
        file_hash,
        submitted_at,
        payload,
        updated_at
    )
    VALUES (
        p_student_id,
        p_exam_room_id,
        p_file_hash,
        p_submitted_at,
        p_payload,
        NOW()
    )
    ON CONFLICT (student_id, exam_room_id) 
    DO UPDATE SET
        file_hash = EXCLUDED.file_hash,
        payload = EXCLUDED.payload,
        submitted_at = EXCLUDED.submitted_at,
        updated_at = NOW()
    WHERE EXCLUDED.submitted_at > public.submissions.submitted_at
    RETURNING id INTO v_sub_id;

    IF FOUND THEN
        RETURN QUERY SELECT v_sub_id, 'upserted'::TEXT;
    ELSE
        SELECT id INTO v_sub_id FROM public.submissions WHERE student_id = p_student_id AND exam_room_id = p_exam_room_id;
        RETURN QUERY SELECT v_sub_id, 'skipped'::TEXT;
    END IF;
END;
$$;

-- 3. RPC b: Pull dữ liệu biến động từ mốc Cursor
CREATE OR REPLACE FUNCTION public.rpc_pull_exam_changes(
    p_last_sync_cursor TIMESTAMPTZ,
    p_room_id UUID
)
RETURNS JSONB
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
DECLARE
    v_result JSONB;
BEGIN
    SELECT jsonb_build_object(
        'exams', COALESCE((
            SELECT jsonb_agg(to_jsonb(e))
            FROM public.exams e
            WHERE e.updated_at > p_last_sync_cursor
        ), '[]'::jsonb),
        'exam_rooms', COALESCE((
            SELECT jsonb_agg(to_jsonb(r))
            FROM public.exam_rooms r
            WHERE r.id = p_room_id AND r.updated_at > p_last_sync_cursor
        ), '[]'::jsonb),
        'submissions', COALESCE((
            SELECT jsonb_agg(to_jsonb(s))
            FROM public.submissions s
            WHERE s.exam_room_id = p_room_id AND s.updated_at > p_last_sync_cursor
        ), '[]'::jsonb),
        'server_time', NOW()
    ) INTO v_result;

    RETURN v_result;
END;
$$;

-- 4. RPC c: Push mảng các sự kiện local idempotency
CREATE OR REPLACE FUNCTION public.rpc_push_local_events(
    p_events JSONB[]
)
RETURNS TABLE (accepted_count INT, rejected_count INT, errors JSONB[])
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
DECLARE
    v_elem JSONB;
    v_accepted INT := 0;
    v_rejected INT := 0;
    v_errors JSONB[] := ARRAY[]::JSONB[];
    v_event_id TEXT;
    v_event_type TEXT;
BEGIN
    FOREACH v_elem IN ARRAY p_events LOOP
        BEGIN
            v_event_id := v_elem->>'event_id';
            v_event_type := v_elem->>'event_type';

            INSERT INTO public.sync_events_log (event_id, event_type, payload, created_at)
            VALUES (v_event_id, v_event_type, v_elem->'payload', NOW())
            ON CONFLICT (event_id) DO NOTHING;

            v_accepted := v_accepted + 1;
        EXCEPTION WHEN OTHERS THEN
            v_rejected := v_rejected + 1;
            v_errors := array_append(v_errors, jsonb_build_object('event_id', v_event_id, 'error', SQLERRM));
        END;
    END LOOP;

    RETURN QUERY SELECT v_accepted, v_rejected, v_errors;
END;
$$;

COMMIT;
