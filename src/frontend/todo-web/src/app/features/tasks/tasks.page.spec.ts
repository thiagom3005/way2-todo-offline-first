import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import Dexie from 'dexie';
import { AuthService, SESSION_STORAGE_KEY } from '../../core/auth/auth.service';
import { LocalDatabase } from '../../core/offline/local-database';
import { LocalTaskRepository } from '../../core/offline/local-task-repository';
import { TodoLocalDb } from '../../core/offline/todo-local-db';
import { newUuid } from '../../core/offline/uuid';
import { SYNC_API } from '../../core/sync/http-sync-api';
import { DEFAULT_SYNC_TIMING, SYNC_TIMING, SyncService } from '../../core/sync/sync.service';
import { FakeSyncServer } from '../../core/sync/testing/fake-sync-server';
import { SyncDevice } from '../../core/sync/testing/sync-device';
import { TasksPage } from './tasks.page';

describe('TasksPage', () => {
  let userId: string;
  let server: FakeSyncServer;
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
  const statusState = () =>
    el.querySelector('[data-testid="sync-status"]')!.getAttribute('data-state');
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
    // Por padrão a API está fora do ar: os testes abaixo exercitam o comportamento local/offline.
    server = new FakeSyncServer();
    server.reachable = false;
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
        { provide: SYNC_API, useFactory: () => server.api() },
        {
          provide: SYNC_TIMING,
          useValue: {
            ...DEFAULT_SYNC_TIMING,
            debounceMs: 10,
            backoffBaseMs: 20,
            backoffMaxMs: 100,
          },
        },
      ],
    });
    fixture = TestBed.createComponent(TasksPage);
    el = fixture.nativeElement;
    await stable();
  });

  afterEach(async () => {
    fixture.destroy();
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
    await until(() => expect(statusState()).toBe('offline'));
    expect(statusText()).toContain('Sem conexão com o servidor');
    expect(statusText()).toContain('2 alterações salvas neste dispositivo');
  });

  it('com a API disponível, a tarefa criada é enviada e o indicador mostra "sincronizado"', async () => {
    server.reachable = true;
    await addTask('Regar plantas', 'Casa');

    await until(() => expect(statusState()).toBe('synced'));
    expect(server.log.map((change) => change.task.title)).toEqual(['Regar plantas']);
    expect(card('Regar plantas').textContent).not.toContain('não sincronizada');
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
    await until(() => expect(statusText()).toContain('Nenhuma alteração pendente'));
  });

  it('filtro de categoria volta para "todas" quando a última tarefa da categoria some', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    await addTask('Regar plantas', 'Casa');
    await addTask('Ligar para cliente', 'Trabalho');
    await until(() => expect(sectionKeys()).toEqual(['category:casa', 'category:trabalho']));

    const categoryFilter = el.querySelector<HTMLSelectElement>('select[aria-label="Categoria"]')!;
    categoryFilter.value = 'trabalho';
    categoryFilter.dispatchEvent(new Event('change'));
    await until(() => expect(sectionKeys()).toEqual(['category:trabalho']));

    card('Ligar para cliente').querySelector<HTMLButtonElement>('button.danger')!.click();

    await until(() => expect(sectionKeys()).toEqual(['category:casa']));
    expect(categoryFilter.value).toBe('');
  });

  it('conflito com outro dispositivo abre o painel e "Manter minha alteração" envia a versão local', async () => {
    server.reachable = true;
    await addTask('Original', 'Casa');
    await until(() => expect(statusState()).toBe('synced'));

    const other = new SyncDevice(`other-${newUuid()}`, server);
    try {
      await other.sync();
      const [remote] = await other.db.local_tasks.toArray();
      await other.repo.update(remote.id, { ...remote, title: 'Editada no outro dispositivo' });
      await other.sync();

      (card('Original').querySelector('button') as HTMLButtonElement).click(); // Editar
      await stable();
      await fill('title', 'Editada nesta tela');
      el.querySelector('app-task-form form')!.dispatchEvent(new Event('submit'));

      await until(() => expect(statusState()).toBe('conflict'));
      const panel = el.querySelector('[data-testid="conflicts"]')!;
      expect(panel.textContent).toContain('Editada no outro dispositivo');
      expect(card('Editada nesta tela').textContent).toContain('conflito');

      Array.from(panel.querySelectorAll('button'))
        .find((b) => b.textContent!.includes('Manter minha alteração'))!
        .click();

      await until(() => expect(statusState()).toBe('synced'));
      expect(server.tasks.get(remote.id)).toMatchObject({
        title: 'Editada nesta tela',
        version: 3,
      });
      expect(el.querySelector('[data-testid="conflicts"]')).toBeNull();
    } finally {
      other.db.close();
      await Dexie.delete(other.db.name);
    }
  });

  it('"Entrar novamente" leva ao login sem encerrar a sessão (offline, as tarefas continuam acessíveis)', async () => {
    server.reachable = true;
    server.authExpired = true; // 401 com o token ainda no prazo local
    await until(() => expect(statusState()).toBe('auth_required'));
    expect(statusText()).toContain('Sessão expirada. Entre novamente para sincronizar.');
    await addTask('Feita sem sessão válida', 'Campo');
    await until(() => expect(statusText()).toContain('1 alteração salva neste dispositivo'));

    Array.from(el.querySelectorAll('button'))
      .find((b) => b.textContent?.trim() === 'Entrar novamente')!
      .click();

    await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/login'));
    expect(localStorage.getItem(SESSION_STORAGE_KEY)).not.toBeNull();
    const auth = TestBed.inject(AuthService);
    expect(auth.user()?.id).toBe(userId);
    expect(auth.needsSignIn()).toBe(true); // o guestGuard deixa a tela de login abrir
  });

  it('o formulário só limpa depois que a tarefa foi gravada no dispositivo', async () => {
    const create = vi
      .spyOn(LocalTaskRepository.prototype, 'create')
      .mockRejectedValueOnce(new DOMException('Cota excedida (simulado).', 'QuotaExceededError'));
    const title = () =>
      el.querySelector<HTMLInputElement>('app-task-form [data-testid="title"]')!.value;

    await addTask('Não perder este texto', 'Campo');
    await until(() => expect(el.querySelector('p.error')?.textContent).toContain('Não foi possível salvar'));
    expect(title()).toBe('Não perder este texto');

    el.querySelector('app-task-form form')!.dispatchEvent(new Event('submit'));
    await until(() => expect(titlesIn('category:campo')).toEqual(['Não perder este texto']));
    expect(title()).toBe('');
    expect(create).toHaveBeenCalledTimes(2);
  });

  it('lista vazia enquanto sincroniza não afirma que não há tarefas', async () => {
    await until(() => expect(el.querySelector('.empty')?.textContent).toContain('Nenhuma tarefa ainda'));
    // Primeira sincronização de um dispositivo novo: a resposta do servidor ainda não chegou.
    vi.spyOn(TestBed.inject(SYNC_API), 'health').mockReturnValue(new Promise(() => {}));

    void TestBed.inject(SyncService).syncNow();

    await until(() =>
      expect(el.querySelector('.empty')?.textContent).toContain('Buscando suas tarefas no servidor'),
    );
  });

  it('a busca tem nome acessível além do placeholder', () => {
    expect(el.querySelector('[data-testid="filter-text"]')!.getAttribute('aria-label')).toBe(
      'Buscar no título ou descrição',
    );
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
