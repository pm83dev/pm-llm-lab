// Modelli TypeScript che riflettono gli eventi LabEvent emessi dal backend.

export type LabEventType =
  | 'runstarted'
  | 'runcompleted'
  | 'runfailed'
  | 'roundstarted'
  | 'roundcompleted'
  | 'promptsent'
  | 'token'
  | 'tokenprob'
  | 'toolcalldelta'
  | 'toolcalldetected'
  | 'toolexecuting'
  | 'toolresult'
  | 'finalanswer';

export interface ToolCallView {
  functionName: string;
  argumentsJson: string;
  id: string;
}

export interface PromptMessageView {
  role: string;
  content: string;
  toolCalls?: ToolCallView[];
  toolCallId?: string;
}

export interface RunStartedData {
  prompt: string;
  toolCount: number;
  toolNames: string[];
}

export interface RunCompletedData {
  totalRounds: number;
  totalDurationMs: number;
  finalMessage?: string;
}

export interface RunFailedData {
  error: string;
  details?: string;
}

export interface RoundStartedData {
  round: number;
  historyCount: number;
  toolCount: number;
}

export interface RoundCompletedData {
  round: number;
  durationMs: number;
  hasToolCalls: boolean;
}

export interface PromptSentData {
  round: number;
  messageCount: number;
  estimatedTokens: number;
  messages: PromptMessageView[];
  toolNames: string[];
}

export interface TokenData {
  text: string;
}

// Un token con la sua probabilità (0..1) e le top alternative al suo posto.
export interface TokenAlternative {
  token: string;
  prob: number; // 0..1
}

export interface TokenProbData {
  token: string;
  prob: number; // 0..1 (exp del logprob)
  alternatives: TokenAlternative[]; // top-5, ordinate per probabilità decrescente
}

export interface ToolCallDeltaData {
  index: number;
  id?: string;
  name?: string;
  argumentsDelta: string;
}

export interface ToolCallDetectedData {
  functionName: string;
  argumentsJson: string;
  id: string;
}

export interface ToolExecutingData {
  functionName: string;
  argumentsJson: string;
  id: string;
}

export interface ToolResultData {
  functionName: string;
  id: string;
  output: string;
  isError: boolean;
  durationMs: number;
}

export interface FinalAnswerData {
  content: string;
  totalRounds: number;
  totalDurationMs: number;
}

// Evento generico ricevuto dal flusso SSE.
export interface LabEvent {
  type: LabEventType;
  round?: number;
  timestamp: number;
  data: any;
}

// ============================================================================
// Modello della timeline: ogni "step" è un nodo visualizzato nel frontend.
// ============================================================================

export type StepKind =
  | 'run-separator'
  | 'run-start'
  | 'round-start'
  | 'prompt'
  | 'streaming'
  | 'tool-call'
  | 'tool-exec'
  | 'tool-result'
  | 'final-answer'
  | 'run-complete'
  | 'run-failed';

export interface TimelineStep {
  id: number;
  kind: StepKind;
  round?: number;
  title: string;
  detail?: string;
  // Per 'prompt': la history inviata al modello.
  promptData?: PromptSentData;
  // Per 'streaming': il testo accumulato token-by-token.
  streamingText?: string;
  // Per 'final-answer' (solo se il client ha richiesto i logprobs): i token con probabilità.
  tokenProbs?: TokenProbData[];
  // Per 'tool-call' / 'tool-exec' / 'tool-result'.
  toolName?: string;
  toolArgs?: string;
  toolOutput?: string;
  toolIsError?: boolean;
  toolDurationMs?: number;
  // Per 'final-answer'.
  finalContent?: string;
  // Per 'run-complete'.
  totalRounds?: number;
  totalDurationMs?: number;
  // Per 'run-failed'.
  error?: string;
  errorDetails?: string;
  // Stato di esecuzione per mostrare spinner.
  isPending?: boolean;
  timestamp: number;
}

// ============================================================================
// Suite di test (eval): modelli che riflettono la risposta di POST /api/lab/eval.
// ============================================================================

export interface EvalCheck {
  type: string;      // contains | notContains | regex | toolCalled | maxRounds
  value: string;
}

export interface EvalCase {
  name: string;
  prompt: string;
  checks: EvalCheck[];
}

export interface EvalCheckResult {
  type: string;
  value: string;
  passed: boolean;
  reason?: string | null;   // motivo del fallimento (null se il check passa)
}

export interface EvalCaseResult {
  name: string;
  passed: boolean;
  checkResults: EvalCheckResult[];
  finalAnswer?: string | null;
  toolsCalled: string[];
  rounds: number;
  durationMs: number;
}

export interface EvalSuiteResult {
  cases: EvalCaseResult[];
  passedCount: number;
  failedCount: number;
  totalDurationMs: number;
}

// Confronto A/B della suite di test (eval): configurazione opzionale per esecuzione e diff per caso.
export interface EvalRunConfig {
  temperature?: number | null;
  systemPrompt?: string | null;
  maxRounds?: number | null;
}

export type EvalCompareVariation = 'migliorato' | 'peggiorato' | 'uguale';

export interface EvalCompareCaseDiff {
  name: string;
  passedA: boolean;
  passedB: boolean;
  variation: EvalCompareVariation;
}

export interface EvalCompareResult {
  a: EvalSuiteResult;
  b: EvalSuiteResult;
  diff: EvalCompareCaseDiff[];
}
