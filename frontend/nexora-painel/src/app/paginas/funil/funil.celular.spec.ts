import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { LARGURA_CELULAR, RealtimeFalso, rotaFalsa } from '../telas-do-painel';
import { Funil } from './funil';

/** ===================== O FUNIL NO DEDO (MOB-2) =====================
 *  HTML5 drag-and-drop NÃO funciona em toque — `dragstart` não dispara, porque o gesto de arrastar
 *  é lido como rolagem. O DES-4 mediu isso e registrou o que ficou faltando: dizer na tela. O
 *  vendedor tentava arrastar, o card não se movia, e nada explicava.
 *
 *  ⚠️ O QUADRO NÃO VIROU ABAS DE ETAPA, e é o ponto. Kanban existe para a leitura lado a lado:
 *  quem só enxerga "Negociação" perde que há 40 cards parados em "Novo Lead". O que entrou foi
 *  conforto para chegar em cada coluna, e um caminho de movimentação que funciona no dedo.
 *  ==================================================================== */
describe('funil no celular', () => {
  const QUADRO = {
    colunas: [
      {
        etapaId: 1, nome: 'Novo Lead', ordem: 1, cor: '#14432F', eGanho: false,
        total: 1, valorTotal: 0, concluidas: 0, temMais: false,
        contatos: [{
          id: 9, nome: 'Marcos Antunes', telefone: '5584988887777', valor: null,
          responsavelId: null, responsavelNome: null, naoLidas: 0, aguardandoDesde: null,
          ordemKanban: 1, versao: 1, vendasEmAberto: 0, canalDoCiclo: null
        }]
      },
      {
        etapaId: 2, nome: 'Negociação', ordem: 2, cor: '#1D5B3F', eGanho: false,
        total: 0, valorTotal: 0, concluidas: 0, temMais: false, contatos: []
      },
      {
        etapaId: 3, nome: 'Venda', ordem: 3, cor: '#2E7A56', eGanho: true,
        total: 0, valorTotal: 0, concluidas: 0, temMais: false, contatos: []
      }
    ]
  };

  let http: HttpTestingController;
  let palco: HTMLElement;

  function montar() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '**', component: Vazio }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso },
        { provide: ActivatedRoute, useValue: rotaFalsa() }
      ]
    });
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: { id: 1, nome: 'Ana', email: 'a@x.com', papel: 'dono', empresaNome: 'Padaria' }
    } as never);

    palco = document.createElement('div');
    palco.style.width = `${LARGURA_CELULAR}px`;
    palco.style.height = '700px';
    document.body.appendChild(palco);

    const f = TestBed.createComponent(Funil);
    palco.appendChild(f.nativeElement);
    f.detectChanges();
    http.match(r => r.url.includes('/funil')).forEach(r => r.flush(QUADRO));
    http.match(() => true).forEach(r => r.flush({
      naoLidas: 0, whatsappConectado: true, semaforoAmareloMinutos: 60,
      semaforoVermelhoMinutos: 240, janelaHoraInicio: 8, janelaHoraFim: 20,
      janelaDiasSemana: 126, feriadosRecentes: []
    }));
    f.detectChanges();
    return f;
  }

  afterEach(() => { palco?.remove(); localStorage.clear(); TestBed.resetTestingModule(); });

  it('O QUADRO CONTINUA ROLANDO NA HORIZONTAL — não virou abas de etapa', () => {
    const raiz = (montar().nativeElement as HTMLElement);
    expect(raiz.querySelectorAll('.coluna').length)
      .withContext('as três etapas deveriam existir lado a lado no quadro')
      .toBe(3);
  });

  it('a rolagem PARA numa coluna inteira (scroll snap)', () => {
    // Sem snap o arrasto para no meio de duas colunas e o vendedor lê metade de cada uma.
    const raiz = (montar().nativeElement as HTMLElement);
    const quadro = raiz.querySelector('.quadro') as HTMLElement;
    const coluna = raiz.querySelector('.coluna') as HTMLElement;

    expect(getComputedStyle(quadro).scrollSnapType)
      .withContext('o quadro não tem parada por coluna').toContain('x');
    expect(getComputedStyle(coluna).scrollSnapAlign)
      .withContext('a coluna não é ponto de parada').toBe('start');
  });

  it('O INDICADOR mostra as etapas que estão fora da tela', () => {
    const raiz = (montar().nativeElement as HTMLElement);
    const pilulas = [...raiz.querySelectorAll('.indicador-etapas .aba')];
    expect(pilulas.map(p => p.textContent!.trim().split(/\s+/)[0]))
      .toEqual(['Novo', 'Negociação', 'Venda']);
  });

  // ================================================================ mover sem arrastar
  it('MOVER PARA… muda a etapa sem nenhum arrasto', () => {
    const f = montar();
    const raiz = f.nativeElement as HTMLElement;

    (raiz.querySelector('.link-editar.mover') as HTMLButtonElement).click();
    f.detectChanges();

    const alvos = [...raiz.querySelectorAll('.etapa-alvo')] as HTMLButtonElement[];
    expect(alvos.length).withContext('o menu de etapas não abriu').toBe(3);

    // A etapa ATUAL continua na lista, desabilitada: tirá-la mudaria as posições conforme a
    // coluna de origem, e o dedo erraria por decorar o lugar errado.
    expect(alvos[0].disabled).withContext('a etapa atual deveria estar desabilitada').toBeTrue();

    alvos[1].click();   // Negociação
    f.detectChanges();

    const req = http.expectOne(r => r.url.includes('/mover') || r.method === 'PATCH' || r.method === 'PUT');
    expect(req.request.body.etapaId ?? req.request.body.etapaDestinoId)
      .withContext('o pedido não levou a etapa de destino').toBe(2);
    req.flush({ ordemKanban: 1 });

    // O card sai da coluna de origem na hora — o mesmo movimento otimista do arrasto.
    f.detectChanges();
    const primeira = raiz.querySelectorAll('.coluna')[0];
    expect(primeira.querySelectorAll('.card').length)
      .withContext('o card não saiu da coluna de origem').toBe(0);
  });

  it('MOVER PARA A ETAPA DE GANHO abre o fechamento, não move direto', () => {
    // ⚠️ A MESMA REGRA DO ARRASTO: a API recusa `mover` para etapa de ganho, de propósito. O card
    // só sai do lugar depois de a venda ser confirmada.
    const f = montar();
    const raiz = f.nativeElement as HTMLElement;

    (raiz.querySelector('.link-editar.mover') as HTMLButtonElement).click();
    f.detectChanges();
    ([...raiz.querySelectorAll('.etapa-alvo')] as HTMLButtonElement[])[2].click();   // Venda
    f.detectChanges();

    http.expectNone(r => r.url.includes('/mover'));
    expect(raiz.querySelector('app-modal-fechamento'))
      .withContext('mover para Venda deveria abrir o modal de fechamento').not.toBeNull();
  });
});

@Component({ template: '' })
class Vazio { }
