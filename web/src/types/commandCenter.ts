export type ItemPriority = "Low" | "Normal" | "High" | "Urgent";
export type Severity = "Info" | "Warning" | "Critical";

export interface DailyBriefing { greeting: string; summary: string; highlights: string[]; attentionCount: number; generatedAt: string; }
export interface PersonalTask { id: string; title: string; details?: string | null; list: string; priority: ItemPriority; dueAt?: string | null; completed: boolean; createdAt: string; }
export interface CalendarEntry { id: string; title: string; startsAt: string; endsAt?: string | null; calendar: string; location?: string | null; url?: string | null; allDay: boolean; }
export interface QuickNote { id: string; title: string; body: string; tags: string[]; pinned: boolean; updatedAt: string; }
export interface ShoppingItem { id: string; name: string; list: string; quantity: number; completed: boolean; createdAt: string; }
export interface TrackedPackage { id: string; carrier: string; trackingNumber: string; description: string; status: string; estimatedDelivery?: string | null; updatedAt: string; }
export interface MediaRequestItem { id: string; title: string; mediaType: string; status: string; requestedBy: string; requestedAt: string; artworkUrl?: string | null; }
export interface NotificationAction { label: string; tool: string; target?: string | null; requiresConfirmation: boolean; }
export interface CommandCenterNotification { id: string; severity: Severity; source: string; title: string; message: string; createdAt: string; acknowledged: boolean; snoozedUntil?: string | null; actions?: NotificationAction[] | null; }
export interface IntegrationStatus { id: string; kind: string; name: string; enabled: boolean; connected: boolean; status: string; lastCheckedAt?: string | null; capabilities: string[]; baseUrl?: string | null; hasSecret: boolean; }
export interface HomeEntity { id: string; name: string; domain: string; state: string; area?: string | null; attributes: Record<string, string>; updatedAt: string; }
export interface OperationalAsset { id: string; category: string; name: string; status: string; detail?: string | null; metrics: Record<string, string>; updatedAt: string; url?: string | null; }
export interface AutomationRule { id: string; name: string; trigger: string; condition?: string | null; actionTool: string; actionTarget?: string | null; enabled: boolean; lastRunAt?: string | null; lastResult?: string | null; }
export interface HouseholdProfile { id: string; displayName: string; role: string; color?: string | null; active: boolean; }
export interface CommandCenterActivity { id: string; tool: string; target?: string | null; message: string; occurredAt: string; succeeded: boolean; }

export interface CommandCenterSnapshot {
  generatedAt: string;
  activeMode: string;
  briefing: DailyBriefing;
  tasks: PersonalTask[];
  calendar: CalendarEntry[];
  notes: QuickNote[];
  shopping: ShoppingItem[];
  packages: TrackedPackage[];
  mediaRequests: MediaRequestItem[];
  inbox: CommandCenterNotification[];
  integrations: IntegrationStatus[];
  homeEntities: HomeEntity[];
  assets: OperationalAsset[];
  automations: AutomationRule[];
  profiles: HouseholdProfile[];
  activity: CommandCenterActivity[];
}

export interface CommandCenterItemRequest { kind: string; id?: string | null; title: string; details?: string | null; category?: string | null; date?: string | null; fields?: Record<string, string>; }
export interface CommandCenterActionRequest { tool: string; target?: string | null; confirmed?: boolean; arguments?: Record<string, string>; }
export interface CommandCenterActionResult { succeeded: boolean; message: string; requiresConfirmation: boolean; auditId?: string | null; }
export interface SearchResult { id: string; kind: string; title: string; subtitle?: string | null; action?: string | null; score: number; }
export interface AssistantSuggestion { label: string; prompt: string; }
export interface AssistantResponse { message: string; suggestions: AssistantSuggestion[]; proposedActions: CommandCenterActionRequest[]; generatedAt: string; }
export interface FileWorkspaceEntry { name: string; path: string; isDirectory: boolean; sizeBytes: number; updatedAt: string; }
export interface SystemLogEntry { occurredAt: string; level: string; source: string; message: string; }
