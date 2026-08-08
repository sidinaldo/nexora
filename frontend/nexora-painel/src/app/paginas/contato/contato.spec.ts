import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { Contato } from './contato';

/** Criar lembrete COM HORA pela tela.
 *
 *  ===================== O BUG QUE ISTO GUARDA =====================
 *  `<input type="time">` produz `"14:30"`, sem segundos — é o que a especificação do HTML define.
 *  A API exigia `"14:30:00"` e devolvia 400, então o lembrete com hora NUNCA era criado. O
 *  vendedor não abria chamado: concluía que o sistema não presta.
 *
 *  A correção foi na API (conversor que aceita as duas formas). Este teste fixa o outro lado do
 *  contrato: a tela manda o formato CURTO, que é o que o navegador de fato produz. Se alguém
 *  "consertar" aqui mandando `"14:30:00"`, o teste avisa que o conserto foi no lugar errado.
 *  ================================================================= */
describe('Contato — lembrete com hora', () => {
  class RealtimeFalso {
    conectado = signal(true);
    mensagemRecebida$ = new Subject<never>();
    conversaAberta$ = new Subject<never>();
    contatoCriado$ = new Subject<never>();
    statusMensagem$ = new Subject<never>();
    conexaoMudou$ = new Subject<never>();
    async conectar() { }
    desconectar() { }
  }

  let httpMock: HttpTestingController;

  /** Mesmo superset do `paginas.render.spec.ts`: campo a mais o JavaScript ignora, e o que
   *  importa é nenhuma lista chegar `undefined` — o que estouraria por culpa do teste. */
  const CORPO = {
    itens: [], temMais: false, total: 0, colunas: [], etapas: [], lembretes: [],
    contato: {
      id: 7, nome: 'Cliente Teste', telefone: '5584900000000', email: null,
      origem: 'manual', responsavelId: null, valor: null, etapaId: 1, etapaNome: 'Novo Lead',
      ganhoEm: null, perdidoEm: null, criadoEm: '2026-08-01T10:00:00Z',
      conversaId: null, aguardandoDesde: null, naoLidas: 0, ordemKanban: 1000
    },
    origemDetalhe: null, observacoes: null, motivoPerda: null, anonimizadoEm: null,
    ultimaMensagemEm: null
  };

  const RESPONDEM_ARRAY = ['/equipe', '/feriados', '/lembretes/contato/', '/vendas', '/trilha/'];

  function responderTudo() {
    for (let volta = 0; volta < 5; volta++) {
      const pendentes = httpMock.match(() => true);
      if (pendentes.length === 0) return;
      pendentes.forEach(r =>
        r.flush(RESPONDEM_ARRAY.some(u => r.request.url.includes(u)) ? [] : CORPO));
    }
  }


  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              paramMap: convertToParamMap({ id: '7' }),
              queryParamMap: convertToParamMap({}),
              data: {}
            }
          }
        }
      ]
    });

    httpMock = TestBed.inject(HttpTestingController);
    TestBed.inject(AuthServico).aplicarLogin({
      token: 't',
      usuario: { id: 1, nome: 'Ana', email: 'a@a.com', papel: 'dono', empresaNome: 'X' }
    } as never);
  });

  afterEach(() => localStorage.clear());

  it('MANDA A HORA NO FORMATO DO NAVEGADOR ("14:30"), e a API aceita', () => {
    const fixture = TestBed.createComponent(Contato);
    fixture.detectChanges();

    responderTudo();
    fixture.detectChanges();

    const c = fixture.componentInstance;
    c.abrirLembrete();
    c.lTitulo.set('Ligar de volta');
    c.lData.set('2026-08-06');
    c.lHora.set('14:30');          // exatamente o que o <input type="time"> entrega
    c.salvarLembrete();

    const req = httpMock.expectOne(r => r.url.includes('/lembretes') && r.method === 'POST');
    const corpo = req.request.body as { horaAlvo: string; dataAlvo: string };

    expect(corpo.horaAlvo).withContext('a tela não deve reformatar a hora').toBe('14:30');
    expect(corpo.dataAlvo).toBe('2026-08-06');

    // A API responde 200 — antes desta correção, respondia 400 aqui.
    req.flush({ id: 1 });
    expect(c.modalLembrete()).withContext('o modal fecha quando salva').toBeFalse();
  });

  /** ===================== O SELECT DE ETAPA MENTIA =====================
   *
   *  Relatado assim: "o contato Ysia está em Negociação, mas na tela de contato está em Novo
   *  Lead". O banco estava certo e a trilha também; quem mentia era o `<select>`.
   *
   *  A causa é de DOM, não de Angular: `[value]` num `<select>` é aplicado quando o contato
   *  chega, e as `<option>` vêm de OUTRA requisição (`/funil/quadro`). Um select sem a opção
   *  correspondente descarta o valor em silêncio e passa a exibir a primeira — "Novo Lead".
   *  Quando as opções chegam depois, a ligação NÃO roda de novo, porque `c.etapaId` não mudou.
   *
   *  ⚠️ E não era só cosmético: este select é o controle que MOVE o contato de etapa. A tela
   *  dizia "Novo Lead" para todo contato que não estivesse na primeira etapa, e quem confiasse
   *  nela mexeria no funil às cegas.
   *  ===================================================================== */
  it('MOSTRA A ETAPA REAL, mesmo com as opções chegando depois do contato', async () => {
    const fixture = TestBed.createComponent(Contato);
    fixture.detectChanges();

    // A ORDEM É O TESTE. Primeiro o contato — na etapa 3, que não é a primeira — e só depois o
    // quadro com as opções. É a ordem que acontece de fato: são duas requisições paralelas.
    const detalhe = httpMock.expectOne(r => r.url.endsWith('/contatos/7') && r.method === 'GET');
    detalhe.flush({
      ...CORPO,
      contato: { ...CORPO.contato, etapaId: 3, etapaNome: 'Negociação' }
    });
    fixture.detectChanges();

    const quadro = httpMock.expectOne(r => r.url.endsWith('/funil'));
    quadro.flush({
      colunas: [
        { etapaId: 1, nome: 'Novo Lead', eGanho: false, contatos: [], total: 0, valor: 0 },
        { etapaId: 2, nome: 'Proposta', eGanho: false, contatos: [], total: 0, valor: 0 },
        { etapaId: 3, nome: 'Negociação', eGanho: false, contatos: [], total: 0, valor: 0 }
      ]
    });
    fixture.detectChanges();

    responderTudo();
    fixture.detectChanges();

    // ⚠️ `whenStable`, e não só `detectChanges`. O `NgModel` aplica o valor na view por
    // MICROTAREFA (`resolvedPromise.then`) — num app zoneless o `detectChanges` síncrono
    // termina antes disso, e a asserção leria o select ainda vazio. No navegador a
    // microtarefa roda no mesmo instante; aqui ela precisa ser esperada.
    await fixture.whenStable();

    const select = (fixture.nativeElement as HTMLElement)
      .querySelector('#etapa') as HTMLSelectElement;

    expect(select).withContext('o select existe').not.toBeNull();
    // O texto da opção MARCADA, e não `select.value`: com `[ngValue]` o valor do DOM é um id
    // interno do Angular (`"2: 3"`). Quem vê a tela lê o texto.
    expect(select.selectedIndex).withContext('nenhuma opção marcada').toBeGreaterThanOrEqual(0);
    expect(select.options[select.selectedIndex].textContent!.trim())
      .withContext('o select está mostrando a etapa errada').toBe('Negociação');
  });

  it('sem hora, manda null — lembrete só com data continua valendo', () => {
    const fixture = TestBed.createComponent(Contato);
    fixture.detectChanges();
    responderTudo();
    fixture.detectChanges();

    const c = fixture.componentInstance;
    c.abrirLembrete();
    c.lTitulo.set('Retomar contato');
    c.lData.set('2026-08-06');
    c.salvarLembrete();

    const req = httpMock.expectOne(r => r.url.includes('/lembretes') && r.method === 'POST');
    expect((req.request.body as { horaAlvo: string | null }).horaAlvo).toBeNull();
    req.flush({ id: 2 });
  });
});
