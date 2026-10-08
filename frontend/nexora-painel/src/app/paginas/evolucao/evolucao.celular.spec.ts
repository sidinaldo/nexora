import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { PERMISSOES_DE } from '../../nucleo/seguranca/permissoes-de-teste';
import {
  EvolucaoDaEquipe, EvolucaoDoVendedor, MesDaConversao
} from '../../nucleo/servicos/evolucao.servico';
import { LARGURA_CELULAR } from '../telas-do-painel';
import { Evolucao } from './evolucao';

/** ===================== POR QUE ESTE ARQUIVO EXISTE =====================
 *  `paginas.celular.spec.ts` monta TODA tela em 390px — inclusive esta —, mas ali as requisições
 *  são respondidas com vazio. Ou seja: o laço compartilhado mede o ESTADO VAZIO, onde esta tela
 *  não tem nem gráfico nem tabela. As duas coisas que podem andar de lado são justamente as que
 *  só existem COM dados.
 *
 *  ⚠️ SETE PESSOAS E NOMES LONGOS DE PROPÓSITO. A legenda é `flex-wrap` e a tabela tem sete
 *  colunas; medir com duas pessoas de nome curto diria que cabe e não provaria nada.
 *  ======================================================================= */
describe('evolução no celular', () => {
  function mes(m: number, decididos: number, ganhos: number, parcial = false): MesDaConversao {
    return {
      ano: 2026, mes: m, decididos, ganhos,
      conversaoPercentual: decididos === 0 ? null : Math.round(ganhos / decididos * 10000) / 100,
      parcial,
      amostraInsuficiente: decididos > 0 && decididos < 10
    };
  }

  function pessoa(
    nome: string, id: number | null,
    tendencia: EvolucaoDoVendedor['tendencia'], variacao: number | null): EvolucaoDoVendedor {
    const meses = [
      mes(5, 120, 38), mes(6, 118, 41), mes(7, 134, 52),
      mes(8, 127, 49), mes(9, 141, 58), mes(10, 64, 27, true)
    ];

    return {
      usuarioId: id, nome, noNexoraDesde: '2021-03-14T00:00:00Z', mesesNoNexora: 66,
      decididos: 704, ganhos: 265, conversaoPercentual: 37.64,
      variacaoPontos: variacao, tendencia, meses
    };
  }

  const RESPOSTA: EvolucaoDaEquipe = {
    equipe: pessoa('Toda a equipe', null, 'estavel', 0.4),
    pessoas: [
      pessoa('Maria Aparecida Gonçalves', 2, 'melhorando', 12.4),
      pessoa('João Pedro de Albuquerque', 3, 'piorando', -11.8),
      pessoa('Ana Carolina Vasconcelos', 4, 'estavel', 1.1),
      pessoa('Sebastião Rodrigues Filho', 5, 'melhorando', 7.9),
      pessoa('Luiz Fernando Nascimento', 6, 'piorando', -4.2),
      pessoa('Patrícia Hollanda Cavalcanti', 7, 'sem_dados', null),
      pessoa('Sem dono', null, 'sem_dados', null)
    ],
    de: '2026-05-01',
    ate: '2026-10-31'
  };

  let fixture: ComponentFixture<Evolucao>;
  let http: HttpTestingController;
  let palco: HTMLElement;

  function montar() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: {
        id: 1, nome: 'Ana', email: 'a@x.com', papel: 'dono',
        permissoes: PERMISSOES_DE.dono, empresaNome: 'Padaria do Bairro'
      }
    } as never);

    palco = document.createElement('div');
    palco.style.width = `${LARGURA_CELULAR}px`;
    palco.style.overflow = 'hidden';
    document.body.appendChild(palco);

    fixture = TestBed.createComponent(Evolucao);
    http = TestBed.inject(HttpTestingController);
    palco.appendChild(fixture.nativeElement);

    fixture.detectChanges();
    for (const r of http.match(() => true)) r.flush(RESPOSTA);
    fixture.detectChanges();
    TestBed.inject(ApplicationRef).tick();
  }

  afterEach(() => {
    palco?.remove();
    TestBed.resetTestingModule();
  });

  /** Mede o ELEMENTO, não a página: asserção sobre a página inteira falharia por culpa de
   *  qualquer outra coisa, que é o jeito mais rápido de um teste virar ruído que se desliga. */
  function transbordoDe(seletor: string): number {
    const el = (fixture.nativeElement as HTMLElement).querySelector(seletor)!;

    return el.scrollWidth - el.clientWidth;
  }

  /** ⚠️ A TABELA TEM DE ROLAR DENTRO DO CONTAINER, e é o que separa esta tela de `/relatorios`,
   *  que transborda ~150px em 390px (defeito anterior, anotado). Sete colunas em 390px não cabem
   *  de jeito nenhum — a pergunta não é "cabe?", é "quem rola?". Se for a página, a tela inteira
   *  anda de lado e o menu some. */
  it('A TABELA ROLA DENTRO DO CARTÃO, não empurra a página', () => {
    montar();

    const raiz = fixture.nativeElement as HTMLElement;
    expect(raiz.querySelector('.tabela-evolucao')).not.toBeNull();

    const sobra = transbordoDe('.tabela-rolagem');
    expect(sobra).withContext('a tabela larga tem de ter o que rolar').toBeGreaterThan(0);

    expect(transbordoDe('.cartao'))
      .withContext(`o cartão passa ${transbordoDe('.cartao')}px da largura que tem`)
      .toBeLessThanOrEqual(1);
  });

  /** ⚠️ A LEGENDA É A PARTE QUE MAIS CRESCE: um item por pessoa, com o nome inteiro. Sem
   *  `flex-wrap` ela empurraria o cartão do gráfico para fora da tela em qualquer time de verdade. */
  it('A LEGENDA DO GRÁFICO QUEBRA EM LINHAS, em vez de andar de lado', () => {
    montar();

    const chaves = (fixture.nativeElement as HTMLElement)
      .querySelectorAll('.grafico-equipe .chave-botao');
    expect(chaves.length).withContext('sete pessoas, sete chaves').toBe(7);

    expect(transbordoDe('.grafico-equipe .legenda'))
      .withContext(`a legenda passa ${transbordoDe('.grafico-equipe .legenda')}px`)
      .toBeLessThanOrEqual(1);

    expect(transbordoDe('.grafico-equipe'))
      .withContext(`o cartão do gráfico passa ${transbordoDe('.grafico-equipe')}px`)
      .toBeLessThanOrEqual(1);
  });

  /** O SVG tem `viewBox` e largura 100%: ele encolhe junto. Um `width` fixo em px aqui faria o
   *  cartão inteiro andar de lado, e o sintoma apareceria só no telefone. */
  it('O GRÁFICO ENCOLHE COM A TELA, e o eixo de meses acompanha', () => {
    montar();

    const raiz = fixture.nativeElement as HTMLElement;
    const svg = raiz.querySelector('.grafico-equipe svg.grafico') as SVGElement;

    expect(svg.getBoundingClientRect().width).toBeLessThanOrEqual(LARGURA_CELULAR);
    expect(raiz.querySelectorAll('.grafico-equipe .eixo > span').length).toBe(6);
  });
});
