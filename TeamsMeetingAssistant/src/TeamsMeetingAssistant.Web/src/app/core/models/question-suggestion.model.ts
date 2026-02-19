export type QuestionCategory = 'Clarification' | 'Deep-dive' | 'Follow-up' | 'Summary';

export interface QuestionSuggestion {
  id: string;
  question: string;
  rationale: string;
  category: QuestionCategory;
  confidenceScore: number;
  generatedAt: string;
}
