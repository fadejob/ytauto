import type { Action, State, Topic } from "./types";

/**
 * Optimistik yangilanish: tugma bosilishi bilan ilova holati darhol o'zgaradi (server javobini kutmasdan). Server javobi
 * kelgach haqiqiy holat bilan almashtiriladi; xato bo'lsa avvalgi holatga qaytariladi. Faqat natijasi aniq amallar.
 */
export function applyOptimistic(state: State, actions: Action[]): State {
  let topics = state.topics;
  const patch = (id: number, p: Partial<Topic>) => { topics = topics.map((t) => (t.id === id ? { ...t, ...p } : t)); };
  for (const a of actions) {
    switch (a.type) {
      case "publish_now": patch(a.id, { publishNow: true, canPublishNow: false, canUploadReady: false }); break;
      case "cancel_publish_now": patch(a.id, { publishNow: false, canPublishNow: true }); break;
      case "upload_ready": patch(a.id, { uploadRequested: true, canUploadReady: false }); break;
      case "cancel_upload_ready": patch(a.id, { uploadRequested: false, canUploadReady: true }); break;
      case "delete_topic": topics = topics.filter((t) => t.id !== a.id); break;
      case "cancel": patch(a.id, { status: 14, canCancel: false, canPublishNow: false, canRetry: false, publishNow: false }); break;
      case "retry": case "retry_settings": patch(a.id, { status: 0, error: null, canRetry: false }); break;
      default: break;
    }
  }
  return topics === state.topics ? state : { ...state, topics };
}
