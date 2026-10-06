"use client";

import { useCallback, useEffect, useState } from "react";
import { AlertTriangle, CheckCircle2, Clock3, MessageSquare, RefreshCw, ShieldCheck, UserCheck, Users, X } from "lucide-react";
import { api, type MovieCollaboration, type MovieProject } from "@/lib/api";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";

const projectTarget = "MovieProject";

/**
 * The collaboration room. Roles, review requests, comments and assignments are
 * persisted server-side; the permissions shown here are the ones the server
 * actually granted to the signed-in user, not a client-side guess.
 */
export function MovieTeamWorkspace({ project }: { project: MovieProject }) {
  const { user } = useAuth();
  const { t } = useLocale();
  const [collaboration, setCollaboration] = useState<MovieCollaboration | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [commentBody, setCommentBody] = useState("");
  const [reviewerUserId, setReviewerUserId] = useState("");
  const [reviewNote, setReviewNote] = useState("");
  const [reviewIsFinal, setReviewIsFinal] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  const load = useCallback(async () => {
    try {
      setCollaboration(await api.getMovieCollaboration(project.id));
    } catch {
      setCollaboration(null);
    } finally {
      setLoading(false);
    }
  }, [project.id]);

  useEffect(() => {
    void Promise.resolve().then(() => load());
  }, [load]);

  async function submitComment() {
    if (commentBody.trim().length === 0) return;
    setBusy(true);
    setError("");
    setMessage("");
    try {
      setCollaboration(
        await api.addMovieComment(project.id, {
          targetType: projectTarget,
          targetId: project.id,
          body: commentBody.trim(),
        }),
      );
      setCommentBody("");
      setMessage("Note saved against this project.");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The note could not be saved.");
    } finally {
      setBusy(false);
    }
  }

  async function resolve(commentId: string) {
    setBusy(true);
    setError("");
    try {
      setCollaboration(await api.resolveMovieComment(project.id, commentId));
      setMessage("Note resolved.");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The note could not be resolved.");
    } finally {
      setBusy(false);
    }
  }

  async function requestReview() {
    if (!user || !reviewerUserId) return;
    setBusy(true);
    setError("");
    setMessage("");
    try {
      await api.requestMovieReview(project.id, {
        targetType: "project",
        targetId: project.id,
        reviewerUserId,
        isFinal: reviewIsFinal,
        requestNote: reviewNote.trim() || null,
      });
      await load();
      setReviewerUserId("");
      setReviewNote("");
      setReviewIsFinal(false);
      setMessage("Review request saved.");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The review request could not be saved.");
    } finally {
      setBusy(false);
    }
  }

  async function decideReview(reviewId: string, status: "Approved" | "ChangesRequested" | "Rejected") {
    setBusy(true);
    setError("");
    setMessage("");
    try {
      await api.decideMovieReview(project.id, reviewId, { status });
      await load();
      setMessage(status === "Approved" ? "Review approved." : status === "ChangesRequested" ? "Changes requested." : "Review rejected.");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The review decision could not be saved.");
    } finally {
      setBusy(false);
    }
  }

  const team = collaboration?.team ?? [];
  const comments = collaboration?.comments ?? [];
  const reviews = collaboration?.reviews ?? [];
  const assignments = collaboration?.assignments ?? [];
  const openComments = comments.filter((comment) => !comment.resolvedAt);
  const pendingReviews = reviews.filter((review) => review.status !== "Approved" && review.status !== "Rejected");
  const canComment = (collaboration?.currentUserPermissions ?? []).includes("Comment");
  const canRequestReviews = Boolean(user && canComment);
  const canRequestFinalReview = (collaboration?.currentUserPermissions ?? []).includes("FinalApproval");
  const reviewerOptions = user ? team.filter((member) => member.userId !== user.id) : [];
  const canDecide = (review: MovieCollaboration["reviews"][number]) => Boolean(
    user
      && review.reviewerUserId === user.id
      && (review.isFinal ? (collaboration?.currentUserPermissions ?? []).includes("FinalApproval") : (collaboration?.currentUserPermissions ?? []).includes("Approve")),
  );

  return (
    <div className="movie-team-workspace" data-testid="movie-team-workspace">
      <section className="movie-team-command" aria-labelledby="movie-team-title">
        <div>
          <span className="movie-workspace-kicker">{t("movieTeam.commandEyebrow")}</span>
          <h3 id="movie-team-title">{t("movieTeam.commandTitle")}</h3>
          <p>{t("movieTeam.commandText")}</p>
        </div>
        <div className="movie-team-command-mark">
          <Users size={23} />
          <span>{t("movieTeam.serverAuthorized")}</span>
        </div>
      </section>

      <section className="movie-team-summary" aria-label={t("movieTeam.summary")}>
        <TeamMetric label="Members" value={team.length} detail="with project access" />
        <TeamMetric label="Open notes" value={openComments.length} detail={`${comments.length} total`} />
        <TeamMetric label="Reviews" value={reviews.length} detail={`${pendingReviews.length} awaiting decision`} />
        <TeamMetric label="Assignments" value={assignments.length} detail="tracked work" />
      </section>

      {collaboration && (
        <section className="movie-team-boundary">
          <ShieldCheck size={16} />
          <div>
            <strong>Your capabilities on this project</strong>
            <span>
              {collaboration.currentUserPermissions.length > 0
                ? collaboration.currentUserPermissions.join(" · ")
                : "Read-only. The server granted no additional movie capability for this project."}
            </span>
          </div>
        </section>
      )}

      {message && (
        <div className="movie-selects-notice is-success" role="status">
          <CheckCircle2 size={15} />
          <span>{message}</span>
        </div>
      )}
      {error && (
        <div className="movie-selects-notice is-error" role="alert">
          <AlertTriangle size={15} />
          <span>{error}</span>
          <button type="button" onClick={() => setError("")} aria-label="Dismiss error">
            <X size={13} />
          </button>
        </div>
      )}

      <section className="movie-team-section" aria-labelledby="movie-team-members-title">
        <div className="movie-team-section-heading">
          <div>
            <span className="movie-workspace-kicker">{t("movieTeam.members")}</span>
            <h3 id="movie-team-members-title">Who can act on this movie.</h3>
            <p>Membership and role changes are enforced by the API, not by this view.</p>
          </div>
          <button type="button" className="movie-workspace-button is-quiet" onClick={() => void load()}>
            <RefreshCw size={13} /> {t("movieTeam.refresh")}
          </button>
        </div>
        {loading ? (
          <div className="movie-team-empty">
            <Clock3 size={18} />
            <span>Loading collaboration state…</span>
          </div>
        ) : team.length === 0 ? (
          <div className="movie-team-empty">
            <Users size={20} />
            <strong>No team record yet</strong>
            <p>Members appear here once the project owner adds a collaborator.</p>
          </div>
        ) : (
          <ul className="movie-team-members">
            {team.map((member) => (
              <li key={member.id}>
                <div>
                  <strong>{member.displayName}</strong>
                  <small>
                    {member.role}
                    {member.isProjectOwner ? " · project owner" : ""}
                  </small>
                </div>
                <span className="movie-team-permissions">{member.permissions.slice(0, 4).join(" · ") || "Read only"}</span>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="movie-team-section" aria-labelledby="movie-team-notes-title">
        <div className="movie-team-section-heading">
          <div>
            <span className="movie-workspace-kicker">Notes</span>
            <h3 id="movie-team-notes-title">Review notes stay attached.</h3>
            <p>Notes are stored against the project record so the decision history survives the session.</p>
          </div>
          <MessageSquare size={18} />
        </div>
        <div className="movie-team-compose">
          <label htmlFor="movie-team-comment">Add a project note</label>
          <textarea
            id="movie-team-comment"
            value={commentBody}
            rows={3}
            maxLength={4000}
            placeholder={canComment ? "Describe the change you want reviewed." : "Your role can read notes but not add them."}
            onChange={(event) => setCommentBody(event.target.value)}
            disabled={!canComment || busy}
          />
          <div className="movie-team-compose-actions">
            <button
              type="button"
              className="movie-workspace-button is-primary"
              onClick={() => void submitComment()}
              disabled={!canComment || busy || commentBody.trim().length === 0}
            >
              {busy ? "Saving…" : "Save note"}
            </button>
            {!canComment && <span>Your current role does not include the Comment capability.</span>}
          </div>
        </div>
        {comments.length === 0 ? (
          <div className="movie-team-empty">
            <MessageSquare size={20} />
            <strong>No notes yet</strong>
            <p>Notes created by the team will be listed here with their resolution state.</p>
          </div>
        ) : (
          <ul className="movie-team-notes">
            {comments.map((comment) => (
              <li key={comment.id} className={comment.resolvedAt ? "is-resolved" : ""}>
                <header>
                  <strong>{comment.authorDisplayName}</strong>
                  <time dateTime={comment.createdAt}>{new Date(comment.createdAt).toLocaleString()}</time>
                </header>
                <p dir="auto">{comment.body}</p>
                <footer>
                  <span>
                    {comment.targetType} · {comment.mentions.length} mention(s)
                  </span>
                  {comment.resolvedAt ? (
                    <span className="movie-team-resolved">
                      <CheckCircle2 size={12} /> Resolved
                    </span>
                  ) : (
                    <button type="button" className="movie-text-action" onClick={() => void resolve(comment.id)} disabled={busy}>
                      <CheckCircle2 size={12} /> Resolve
                    </button>
                  )}
                </footer>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="movie-team-section" aria-labelledby="movie-team-reviews-title">
        <div className="movie-team-section-heading">
          <div>
            <span className="movie-workspace-kicker">Reviews</span>
            <h3 id="movie-team-reviews-title">Requested sign-off.</h3>
            <p>Each review names its reviewer, whether it is a final gate, and the recorded decision.</p>
          </div>
          <UserCheck size={18} />
        </div>
        {canRequestReviews && reviewerOptions.length > 0 && (
          <div className="movie-team-compose">
            <label htmlFor="movie-team-reviewer">Request project sign-off</label>
            <select id="movie-team-reviewer" value={reviewerUserId} onChange={(event) => setReviewerUserId(event.target.value)} disabled={busy}>
              <option value="">Choose a reviewer</option>
              {reviewerOptions.map((member) => <option key={member.userId} value={member.userId}>{member.displayName} · {member.role}</option>)}
            </select>
            <textarea id="movie-team-review-note" value={reviewNote} rows={2} maxLength={4000} placeholder="What should the reviewer check?" onChange={(event) => setReviewNote(event.target.value)} disabled={busy} />
            <label><input type="checkbox" checked={reviewIsFinal} onChange={(event) => setReviewIsFinal(event.target.checked)} disabled={busy || !canRequestFinalReview} /> <span>Mark this as a final gate request</span></label>
            <div className="movie-team-compose-actions">
              <button type="button" className="movie-workspace-button is-primary" onClick={() => void requestReview()} disabled={busy || !reviewerUserId}>{busy ? "Saving…" : "Request review"}</button>
              {!canRequestFinalReview && <span>Final gate requests require FinalApproval authority.</span>}
            </div>
          </div>
        )}
        {canRequestReviews && reviewerOptions.length === 0 && <div className="movie-team-compose-actions"><span>Add another current team member before requesting a review.</span></div>}
        {reviews.length === 0 ? (
          <div className="movie-team-empty">
            <UserCheck size={20} />
            <strong>No reviews requested</strong>
            <p>A review request appears here once someone asks a named collaborator to sign off.</p>
          </div>
        ) : (
          <ul className="movie-team-reviews">
            {reviews.map((review) => (
              <li key={review.id} className={review.status === "Approved" ? "is-approved" : review.status === "Rejected" ? "is-rejected" : "is-pending"}>
                <div>
                  <strong>
                    {review.reviewerDisplayName}
                    {review.isFinal ? " · final gate" : ""}
                  </strong>
                  <small>
                    Requested by {review.requestedByDisplayName} · {review.targetType}
                    {review.requestNote ? ` · ${review.requestNote}` : ""}
                  </small>
                  {review.decisionNote && <small>Decision note: {review.decisionNote}</small>}
                </div>
                <div className="movie-team-compose-actions">
                  <span className={`movie-team-review-status is-${review.status.toLowerCase()}`}>{review.status}</span>
                  {canDecide(review) && review.status !== "Approved" && review.status !== "Rejected" && (
                    <div className="movie-team-compose-actions">
                      <button type="button" className="movie-text-action" onClick={() => void decideReview(review.id, "Approved")} disabled={busy}><CheckCircle2 size={12} /> Approve</button>
                      <button type="button" className="movie-text-action" onClick={() => void decideReview(review.id, "ChangesRequested")} disabled={busy}>Request changes</button>
                      <button type="button" className="movie-text-action is-danger" onClick={() => void decideReview(review.id, "Rejected")} disabled={busy}><X size={12} /> Reject</button>
                    </div>
                  )}
                </div>
              </li>
            ))}
          </ul>
        )}
      </section>

      {assignments.length > 0 && (
        <section className="movie-team-section" aria-labelledby="movie-team-assignments-title">
          <div className="movie-team-section-heading">
            <div>
              <span className="movie-workspace-kicker">Assignments</span>
              <h3 id="movie-team-assignments-title">Tracked work.</h3>
              <p>Assignments carry an owner, a status and an optional due date.</p>
            </div>
          </div>
          <ul className="movie-team-assignments">
            {assignments.map((assignment) => (
              <li key={assignment.id}>
                <div>
                  <strong>{assignment.title}</strong>
                  <small>
                    {assignment.assigneeDisplayName} · assigned by {assignment.assignedByDisplayName}
                    {assignment.dueAt ? ` · due ${new Date(assignment.dueAt).toLocaleDateString()}` : ""}
                  </small>
                </div>
                <span className="movie-team-assignment-status">{assignment.status}</span>
              </li>
            ))}
          </ul>
        </section>
      )}

      <div className="movie-team-footnote">
        <ShieldCheck size={14} />
        <span>
          Every action on this page is re-authorized by the API against the workspace and project. This view never
          implies a capability the server has not granted.
        </span>
      </div>
    </div>
  );
}

function TeamMetric({ label, value, detail }: { label: string; value: number; detail: string }) {
  return (
    <div className="movie-team-metric">
      <span>{label}</span>
      <strong>{value}</strong>
      <small>{detail}</small>
    </div>
  );
}
