import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import Dexie from 'dexie';
import { SESSION_STORAGE_KEY } from '../../core/auth/auth.service';
import { LocalDatabase } from '../../core/offline/local-database';
import { TodoLocalDb } from '../../core/offline/todo-local-db';
import { newUuid } from '../../core/offline/uuid';
import { TasksPage } from './tasks.page';

describe('TasksPage', () => {
  let userId: string;
  let fixture: ComponentFixture<TasksPage>;
  let el: HTMLElement;

  const stable = () => fixture.whenStable();
  const until = (assertion: () => void) =>
    vi.waitFor(async () => {
      await stable();
      assertion();
    });
  const sectionKeys = () =>
    Array.from(el.querySelectorAll('[data-section]'), (s) => s.getAttribute('data-section'));
  const titlesIn = (key: string) =>
    Array.from(el.querySelectorAll(`[data-section="${key}"] [data-testid="task-title"]`), (t) =>
      t.textContent?.trim(),
    );
  const statusText = () => el.querySelector('[data-testid="sync-status"]')!.textContent!.trim();
  const card = (title: string) =>
    Array.from(el.querySelectorAll('app-task-item')).find((item) =>
      item.querySelector('[data-testid="task-title"]')?.textContent?.includes(title),
    )!;

  async function fill(testId: string, value: string) {
    const input = el.querySelector<HTMLInputElement>(`app-task-form [data-testid="${testId}"]`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await stable();
  }

  async function addTask(title: string, category: string) {
    await fill('title', title);
    await fill('category', category);
    el.querySelector('app-task-form form')!.dispatchEvent(new Event('submit'));
    await stable();
  }

  beforeEach(async () => {
    userId = newUuid();
    localStorage.setItem(
      SESSION_STORAGE_KEY,
      JSON.stringify({
        accessToken: 'header.payload.signature',
        expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
        user: { id: userId, email: 'demo@way2.local' },
      }),
    );
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'login', children: [] }]),
      ],
    });
    fixture = TestBed.createComponent(TasksPage);
    el = fixture.nativeElement;
    await stable();
  });

  afterEach(async () => {
    TestBed.inject(LocalDatabase).close();
    await Dexie.delete(TodoLocalDb.nameFor(userId));
    localStorage.clear();
    vi.restoreAllMocks();
  });

  it('tarefa com URGENTE sobe para a seção Urgentes assim que é salva', async () => {
    await addTask('Comprar café', 'Casa');
    await addTask('Ligar para cliente URGENTE', 'Trabalho');

    await until(() => expect(sectionKeys()).toEqual(['urgent', 'category:casa']));
    expect(titlesIn('urgent')).toEqual(['Ligar para cliente URGENTE']);
    expect(statusText()).toContain('2 alterações salvas apenas neste dispositivo');
  });

  it('editar removendo "urgente" devolve a tarefa ao grupo da categoria', async () => {
    await addTask('Pagar conta urgente', 'Casa');
    await until(() => expect(sectionKeys()).toEqual(['urgent']));

    (card('Pagar conta urgente').querySelector('button') as HTMLButtonElement).click();
    await stable();
    await fill('title', 'Pagar conta');
    el.querySelector('app-task-form form')!.dispatchEvent(new Event('submit'));

    await until(() => expect(sectionKeys()).toEqual(['category:casa']));
    expect(titlesIn('category:casa')).toEqual(['Pagar conta']);
  });

  it('concluir e excluir atualizam a lista a partir do IndexedDB', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    await addTask('Regar plantas', 'Casa');
    await until(() => expect(titlesIn('category:casa')).toEqual(['Regar plantas']));

    card('Regar plantas').querySelector<HTMLInputElement>('input[type="checkbox"]')!.click();
    await until(() =>
      expect(card('Regar plantas').querySelector('.task.completed')).not.toBeNull(),
    );

    card('Regar plantas').querySelector<HTMLButtonElement>('button.danger')!.click();
    await until(() => expect(sectionKeys()).toEqual([]));
    // Criada e excluída antes de qualquer envio: nada fica pendente.
    expect(statusText()).toContain('Nenhuma alteração local pendente');
  });

  it('sair encerra a sessão mas preserva tarefas e pendências do usuário', async () => {
    await addTask('Tarefa offline', 'Campo');
    await until(() => expect(titlesIn('category:campo')).toEqual(['Tarefa offline']));

    Array.from(el.querySelectorAll('button'))
      .find((b) => b.textContent?.trim() === 'Sair')!
      .click();
    await stable();

    expect(localStorage.getItem(SESSION_STORAGE_KEY)).toBeNull();
    const reopened = new TodoLocalDb(userId);
    try {
      expect(await reopened.local_tasks.count()).toBe(1);
      expect(await reopened.local_mutations.count()).toBe(1);
    } finally {
      reopened.close();
    }
  });
});
