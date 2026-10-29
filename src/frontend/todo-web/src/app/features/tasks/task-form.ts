import {
  afterRenderEffect,
  Component,
  computed,
  effect,
  type ElementRef,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import {
  type AbstractControl,
  NonNullableFormBuilder,
  ReactiveFormsModule,
  type ValidationErrors,
  Validators,
} from '@angular/forms';
import {
  DEFAULT_CATEGORY,
  type LocalTask,
  TASK_LIMITS,
  type TaskFields,
  type TaskPriority,
} from '../../core/offline/local-models';
import { isUrgent } from './urgency';

const notBlank = (control: AbstractControl<string>): ValidationErrors | null =>
  control.value?.trim() ? null : { required: true };

@Component({
  selector: 'app-task-form',
  imports: [ReactiveFormsModule],
  template: `
    <form #formElement [formGroup]="form" (ngSubmit)="submit()" class="card stack task-form">
      <div class="form-header">
        <h2>{{ task() ? 'Editar tarefa' : 'Nova tarefa' }}</h2>
        @if (urgent()) {
          <span class="badge urgent" data-testid="urgent-preview">Urgente</span>
        }
      </div>

      <label>
        Título
        <input #titleInput formControlName="title" [maxlength]="limits.title" data-testid="title" />
      </label>
      @if (form.controls.title.touched && form.controls.title.invalid) {
        <small class="error">Informe um título.</small>
      }

      <label>
        Descrição
        <textarea
          formControlName="description"
          rows="2"
          [maxlength]="limits.description"
          data-testid="description"
        ></textarea>
      </label>

      <div class="row">
        <label>
          Vencimento
          <input type="date" formControlName="dueDate" data-testid="due-date" />
        </label>
        <label>
          Prioridade
          <select formControlName="priority" data-testid="priority">
            <option value="low">Baixa</option>
            <option value="medium">Média</option>
            <option value="high">Alta</option>
          </select>
        </label>
      </div>

      <label>
        Categoria
        <input
          formControlName="category"
          list="task-categories"
          [maxlength]="limits.category"
          data-testid="category"
        />
      </label>
      <datalist id="task-categories">
        @for (category of categories(); track category) {
          <option [value]="category"></option>
        }
      </datalist>
      @if (form.controls.category.touched && form.controls.category.invalid) {
        <small class="error">Informe uma categoria.</small>
      }

      <div class="actions">
        @if (task()) {
          <button type="button" (click)="cancelled.emit()">Cancelar</button>
        }
        <button type="submit" class="primary" data-testid="save">
          {{ task() ? 'Salvar alterações' : 'Adicionar tarefa' }}
        </button>
      </div>
    </form>
  `,
})
export class TaskForm {
  /** Tarefa em edição; `null` = criação. */
  readonly task = input<LocalTask | null>(null);
  readonly categories = input<readonly string[]>([]);
  readonly saved = output<TaskFields>();
  readonly cancelled = output<void>();

  protected readonly limits = TASK_LIMITS;
  protected readonly form = inject(NonNullableFormBuilder).group({
    title: ['', [notBlank, Validators.maxLength(TASK_LIMITS.title)]],
    description: ['', Validators.maxLength(TASK_LIMITS.description)],
    dueDate: [''],
    priority: ['medium' as TaskPriority],
    category: [DEFAULT_CATEGORY, [notBlank, Validators.maxLength(TASK_LIMITS.category)]],
  });

  private readonly value = toSignal(this.form.valueChanges, {
    initialValue: this.form.getRawValue(),
  });
  private lastSubmitted: ReturnType<typeof this.form.getRawValue> | null = null;
  private readonly formElement = viewChild.required<ElementRef<HTMLFormElement>>('formElement');
  private readonly titleInput = viewChild.required<ElementRef<HTMLInputElement>>('titleInput');

  /** Prévia imediata da regra de urgência enquanto o usuário digita. */
  protected readonly urgent = computed(() => {
    const { title = '', description = '' } = this.value();
    return isUrgent({ title, description });
  });

  constructor() {
    effect(() => this.load(this.task()));
    // A tarefa editada pode estar bem abaixo na lista: leva o usuário até o formulário.
    afterRenderEffect(() => {
      if (!this.task()) return;
      this.formElement().nativeElement.scrollIntoView({ block: 'start' });
      this.titleInput().nativeElement.focus({ preventScroll: true });
    });
  }

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const value = this.form.getRawValue();
    this.lastSubmitted = value;
    this.saved.emit({
      title: value.title.trim(),
      description: value.description.trim() || null,
      dueDate: value.dueDate || null,
      priority: value.priority,
      status: this.task()?.status ?? 'pending',
      category: value.category.trim(),
    });
  }

  /**
   * Depois de uma criação gravada: limpa o texto e mantém categoria e prioridade para a próxima. Se o
   * usuário já começou outra tarefa enquanto a gravação terminava, nada do que ele digitou é apagado.
   */
  clearForNext(): void {
    const current = this.form.getRawValue();
    const untouched =
      this.lastSubmitted !== null &&
      (Object.keys(current) as (keyof typeof current)[]).every(
        (key) => current[key] === this.lastSubmitted![key],
      );
    if (untouched) this.form.reset({ ...current, title: '', description: '', dueDate: '' });
  }

  private load(task: LocalTask | null): void {
    this.form.reset({
      title: task?.title ?? '',
      description: task?.description ?? '',
      dueDate: task?.dueDate ?? '',
      priority: task?.priority ?? 'medium',
      category: task?.category ?? DEFAULT_CATEGORY,
    });
  }
}
