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
