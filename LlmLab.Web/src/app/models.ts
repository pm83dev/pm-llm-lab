// Modelli TypeScript che riflettono gli eventi LabEvent emessi dal backend.

export type LabEventType =
  | 'runstarted'
  | 'runcompleted'
  | 'runfailed'
  | 'roundstarted'
  | 'roundcompleted'
  | 'promptsent'
  | 'token'
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
